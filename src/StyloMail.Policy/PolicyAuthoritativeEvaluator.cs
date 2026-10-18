using System.Collections.Concurrent;
using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>Per-tenant authoritative configuration. Presence of a tenant opts it in.</summary>
public sealed record PolicyAuthoritativeOptions
{
    public IReadOnlyDictionary<string, string> Tenants { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Bounded fallback Review deadline when no pack can be read.</summary>
    public TimeSpan FallbackReviewDeadline { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Whether the durable review state is available in this deployment.</summary>
    public bool ReviewStateAvailable { get; init; } = true;

    public IReadOnlyDictionary<string, string> Validated()
    {
        foreach (var (tenant, jurisdiction) in Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(jurisdiction))
            {
                throw new ArgumentException(
                    "An authoritative tenant must have a non-blank id and jurisdiction.", nameof(Tenants));
            }
        }

        if (FallbackReviewDeadline < TimeSpan.FromHours(1)
            || FallbackReviewDeadline > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FallbackReviewDeadline), FallbackReviewDeadline, "The fallback review deadline must be between 1 hour and 7 days.");
        }

        return Tenants;
    }
}

/// <summary>What the authoritative resolution found.</summary>
public enum PolicyAuthoritativeKind
{
    Disabled = 0,
    Absent = 1,
    Pack = 2,
    Unavailable = 3,
}

/// <summary>The result of one authoritative resolution.</summary>
public sealed record PolicyAuthoritativeResult
{
    public required PolicyAuthoritativeKind Kind { get; init; }

    public EffectivePolicy? Effective { get; init; }

    public PolicyEvaluation? Evaluation { get; init; }

    public PolicyDecisionRecord? Record { get; init; }

    public DateTimeOffset? FallbackReviewDeadline { get; init; }

    public string? UnavailableReason { get; init; }

    public bool ReviewStateAvailable { get; init; } = true;
}

/// <summary>The authoritative policy port consumed by the assessment composition.</summary>
public interface IPolicyAuthoritativeEvaluator
{
    bool IsEnabled(string tenantId);

    Task<PolicyAuthoritativeResult> EvaluateAsync(
        string tenantId,
        IReadOnlyList<Evidence> evidence,
        PolicyPropertyScope scope,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a tenant's authoritative policy and evaluates it over evidence.
/// </summary>
/// <remarks>
/// The flag is read from the tenant map only. An enabled tenant never silently falls back to legacy:
/// a missing pack is Absent (healthy), while an exception, timeout or malformed read is Unavailable
/// and produces a bounded Review fallback. A flag-off tenant performs no pack, rule, property,
/// binding or cache work.
/// </remarks>
public sealed class PolicyAuthoritativeEvaluator : IPolicyAuthoritativeEvaluator
{
    private const int CacheCapacity = 256;
    private const string RuleSetUnavailable = "policy.rule_set_unavailable";
    private const string PackStoreUnavailable = "policy.pack_store_unavailable";
    private const string BindingStoreUnavailable = "policy.binding_store_unavailable";

    private readonly IPolicyPackReader _packs;
    private readonly IPolicyRuleSetReader _ruleSets;
    private readonly IPolicyPropertyReader _properties;
    private readonly IPolicyBindingReader _bindings;
    private readonly PolicyAuthoritativeOptions _options;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sequence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _installed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastObserved = new(StringComparer.Ordinal);

    public PolicyAuthoritativeEvaluator(
        IPolicyPackReader packs,
        IPolicyRuleSetReader ruleSets,
        IPolicyPropertyReader properties,
        IPolicyBindingReader bindings,
        PolicyAuthoritativeOptions options)
    {
        _packs = packs;
        _ruleSets = ruleSets;
        _properties = properties;
        _bindings = bindings;
        _options = options;
        _options.Validated();
    }

    public bool IsEnabled(string tenantId) => _options.Tenants.ContainsKey(tenantId);

    public string? JurisdictionFor(string tenantId) =>
        _options.Tenants.TryGetValue(tenantId, out var jurisdiction) ? jurisdiction : null;

    public bool ReviewStateAvailable => _options.ReviewStateAvailable;

    /// <summary>Age of the tenant's last successful pack observation, or null.</summary>
    public TimeSpan? CacheAgeFor(string tenantId, DateTimeOffset now) =>
        _lastObserved.TryGetValue(tenantId, out var observed) ? now - observed : null;

    /// <summary>Invalidates the tenant's cache after a pack or binding update.</summary>
    public void Invalidate(string tenantId)
    {
        // Fence every read already in flight, then clear what was installed before the update.
        TryAdvanceInstalled(tenantId, NextGeneration(tenantId));
        ClearCache(tenantId);
    }

    public async Task<PolicyAuthoritativeResult> EvaluateAsync(
        string tenantId,
        IReadOnlyList<Evidence> evidence,
        PolicyPropertyScope scope,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!_options.Tenants.TryGetValue(tenantId, out var jurisdiction))
        {
            return new PolicyAuthoritativeResult { Kind = PolicyAuthoritativeKind.Disabled };
        }

        // A generation is issued in start order, not install order. The newest-starting read wins
        // even when an older read completes first, and a delayed older read cannot overwrite it.
        var generation = NextGeneration(tenantId);
        var cacheKey = $"{tenantId}|{jurisdiction}";

        PolicyPack? pack = null;
        PolicyRuleSet? ruleSet = null;
        var packFromCache = false;
        var packReadFailed = false;
        TimeSpan? cacheAge = null;

        try
        {
            pack = await _packs.GetEffectiveAsync(tenantId, jurisdiction, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed read is Unavailable, never the healthy Absent that a null result means.
            packReadFailed = true;
        }

        if (!packReadFailed && pack is null)
        {
            // A healthy no-pack answer clears the tenant's cache and is legacy, not a fallback, but
            // only if this read is not older than a pack another read already installed.
            InvalidateIfCurrent(tenantId, generation);
            return new PolicyAuthoritativeResult { Kind = PolicyAuthoritativeKind.Absent };
        }

        if (pack is null && _cache.TryGetValue(cacheKey, out var cached))
        {
            var age = now - cached.ObservedAt;
            if (age <= TimeSpan.FromMinutes(5))
            {
                pack = cached.Pack;
                ruleSet = cached.RuleSet;
                packFromCache = true;
                cacheAge = age;
            }
        }

        if (pack is null)
        {
            return Fallback(now, PackStoreUnavailable,
                "The effective pack could not be read and no cache entry is within the five-minute bound. The action is withheld for review.");
        }

        if (ruleSet is null)
        {
            try
            {
                ruleSet = await _ruleSets
                    .FindLiveByVersionAsync(tenantId, pack.RuleSetVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                ruleSet = null;
            }
        }

        if (ruleSet is null)
        {
            return Fallback(now, RuleSetUnavailable,
                $"Rule set '{pack.RuleSetVersion}' is not live or could not be read. The action is withheld for review.");
        }

        PolicyPropertyReadResult propertyRead;
        try
        {
            propertyRead = await _properties
                .ReadAsync(tenantId, scope, PolicyPropertyKeys.All, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            propertyRead = new PolicyPropertyReadResult
            {
                Facts = [],
                MissingKeys = PolicyPropertyKeys.All,
                Unavailable = true,
            };
        }

        IReadOnlyList<PolicyBinding> bindings;
        try
        {
            bindings = await _bindings
                .ListForResolutionAsync(tenantId, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Fallback(now, BindingStoreUnavailable,
                "The binding store could not be read, so the effective policy cannot be established. The action is withheld for review.");
        }

        var chain = Chain(scope);
        var effective = EffectivePolicyResolver.Resolve(
            pack,
            ruleSet,
            bindings,
            propertyRead.Facts,
            propertyRead.MissingKeys,
            chain,
            now);

        var evaluation = new DeterministicPolicyEvaluator().Evaluate(new PolicyEvaluationInput
        {
            TenantId = tenantId,
            Pack = pack,
            RuleSet = ruleSet,
            Evidence = evidence,
            Effective = effective,
        });

        if (!packFromCache)
        {
            Install(tenantId, cacheKey, generation, pack, ruleSet, now);
        }

        var snapshot = effective.ToSnapshot();
        var selectedBindings = effective.Bindings
            .Where(binding => binding.Selected)
            .Select(binding => binding.BindingId)
            .ToArray();

        return new PolicyAuthoritativeResult
        {
            Kind = PolicyAuthoritativeKind.Pack,
            Effective = effective,
            Evaluation = evaluation,
            ReviewStateAvailable = _options.ReviewStateAvailable,
            Record = new PolicyDecisionRecord
            {
                Source = PolicySource.Pack,
                PackId = evaluation.PackId,
                PackVersion = evaluation.PackVersion,
                RuleSetId = evaluation.RuleSetId,
                RuleSetVersion = evaluation.RuleSetVersion,
                ScorerVersion = evaluation.ScorerVersion,
                EvaluatorVersion = DeterministicPolicyEvaluator.Version,
                Verdict = evaluation.Verdict,
                RequiresHumanReview = evaluation.RequiresHumanReview,
                DecidingRuleId = evaluation.DecidingRuleId,
                DecidingRuleVersion = evaluation.DecidingRuleVersion,
                DecidingThreshold = evaluation.DecidingThreshold,
                DecidingReasonCode = evaluation.DecidingReasonCode,
                DecidingReason = evaluation.DecidingReason,
                MissingDimensions = evaluation.MissingDimensions,
                Coverage = evaluation.Coverage,
                BindingIds = selectedBindings,
                PropertyFacts = effective.PropertyFacts,
                EvidenceSnapshotDigest = evaluation.EvidenceSnapshotDigest,
                EffectiveInput = snapshot,
                EffectiveInputDigest = snapshot.Digest,
                EvaluatedAt = now,
                Shadow = false,
                Authoritative = true,
                Applied = false,
                Durable = false,
                Admission = AdmissionOutcome.NotAttempted,
                PackFromCache = packFromCache,
                CacheAge = cacheAge,
                PropertyFactsUnavailable = propertyRead.Unavailable || propertyRead.MissingKeys.Count > 0,
                MissingPropertyKeys = effective.MissingPropertyKeys,
                LegacyAction = null,
                ComposedAction = null,
                FinalAction = null,
            },
        };
    }

    private PolicyAuthoritativeResult Fallback(
        DateTimeOffset now,
        string reasonCode,
        string reason) => new()
        {
            Kind = PolicyAuthoritativeKind.Unavailable,
            UnavailableReason = reasonCode,
            ReviewStateAvailable = _options.ReviewStateAvailable,
            FallbackReviewDeadline = now + _options.FallbackReviewDeadline,
            Record = new PolicyDecisionRecord
            {
                Source = PolicySource.Fallback,
                PackId = null,
                PackVersion = null,
                RuleSetId = null,
                RuleSetVersion = null,
                ScorerVersion = null,
                EvaluatorVersion = DeterministicPolicyEvaluator.Version,
                Verdict = PolicyVerdict.Review,
                RequiresHumanReview = true,
                DecidingRuleId = null,
                DecidingRuleVersion = null,
                DecidingThreshold = null,
                DecidingReasonCode = reasonCode,
                DecidingReason = reason,
                MissingDimensions = [],
                Coverage = 0,
                BindingIds = [],
                PropertyFacts = [],
                EvidenceSnapshotDigest = null,
                EffectiveInput = null,
                EffectiveInputDigest = null,
                EvaluatedAt = now,
                Shadow = false,
                Authoritative = false,
                Applied = false,
                Durable = false,
                Admission = AdmissionOutcome.NotAttempted,
                FallbackAttempted = true,
                PackUnavailable = true,
                FallbackReason = reasonCode,
                PropertyFactsUnavailable = false,
                MissingPropertyKeys = [],
                LegacyAction = null,
                ComposedAction = null,
                FinalAction = null,
            },
        };

    private void Install(
        string tenantId,
        string cacheKey,
        long generation,
        PolicyPack pack,
        PolicyRuleSet ruleSet,
        DateTimeOffset now)
    {
        // The newest-starting successful read wins. An older read that completes later does not
        // install, and a newer read that completes after an older one still does.
        if (!TryAdvanceInstalled(tenantId, generation))
        {
            return;
        }

        if (_cache.Count >= CacheCapacity)
        {
            var oldest = _cache.OrderBy(entry => entry.Value.ObservedAt).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _cache.TryRemove(oldest.Key, out _);
            }
        }

        _cache[cacheKey] = new CacheEntry(pack, ruleSet, now);
        _lastObserved[tenantId] = now;
    }

    private long NextGeneration(string tenantId) =>
        _sequence.AddOrUpdate(tenantId, 1, (_, current) => current + 1);

    private bool TryAdvanceInstalled(string tenantId, long generation)
    {
        while (true)
        {
            var current = _installed.GetOrAdd(tenantId, 0);
            if (generation <= current)
            {
                return false;
            }

            if (_installed.TryUpdate(tenantId, generation, current))
            {
                return true;
            }
        }
    }

    private void InvalidateIfCurrent(string tenantId, long generation)
    {
        if (TryAdvanceInstalled(tenantId, generation))
        {
            ClearCache(tenantId);
        }
    }

    private void ClearCache(string tenantId)
    {
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(tenantId + "|", StringComparison.Ordinal)))
        {
            _cache.TryRemove(key, out _);
        }

        _lastObserved.TryRemove(tenantId, out _);
    }

    private static string[] Chain(PolicyPropertyScope scope)
    {
        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope.ConversationId)) chain.Add($"conversation:{scope.ConversationId}");
        if (!string.IsNullOrWhiteSpace(scope.EntityId)) chain.Add($"entity:{scope.EntityId}");
        if (!string.IsNullOrWhiteSpace(scope.Domain)) chain.Add($"domain:{scope.Domain}");
        if (!string.IsNullOrWhiteSpace(scope.Company)) chain.Add($"company:{scope.Company}");
        return [.. chain];
    }

    private sealed record CacheEntry(
        PolicyPack Pack,
        PolicyRuleSet RuleSet,
        DateTimeOffset ObservedAt);
}
