using System.Collections.Concurrent;
using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>Per-tenant authoritative configuration. Presence of a tenant opts it in.</summary>
public sealed record PolicyAuthoritativeOptions
{
    /// <summary>The hard startup cap on configured authoritative tenants (and therefore fences).</summary>
    public const int MaxConfiguredTenants = 4096;

    public const string TenantCapExceeded = "policy.authoritative_tenant_cap_exceeded";

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

        if (Tenants.Count > MaxConfiguredTenants)
        {
            throw new ArgumentException(
                $"{TenantCapExceeded}: {Tenants.Count} configured authoritative tenants exceed the "
                + $"hard cap of {MaxConfiguredTenants}. Refusing enablement rather than evicting a fence.",
                nameof(Tenants));
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

    /// <summary>Live effective pack/rule pairs. Bounded by <see cref="CacheCapacity"/>.</summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// One generation fence per configured tenant/jurisdiction. Not part of the live cache bound:
    /// a fence must outlive any in-flight read, and is bounded by the configured tenant count.
    /// </summary>
    private readonly ConcurrentDictionary<string, Fence> _fences = new(StringComparer.Ordinal);

    /// <summary>
    /// Read-start ordinals, mirrored for observability. Correctness uses the ordinal and mutation
    /// version carried by the immutable fence entry, not this separate dictionary.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _sequence = new(StringComparer.Ordinal);

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
        var tenants = _options.Validated();
        foreach (var (tenant, jurisdiction) in tenants)
        {
            // Startup constructs exactly one fence per configured tenant/jurisdiction. A tenant
            // removed from config is absent on the next start; no assessment-path removal.
            _fences[CacheKey(tenant, jurisdiction)] = new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0);
        }
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
        if (!_options.Tenants.TryGetValue(tenantId, out var jurisdiction))
        {
            return;
        }

        var cacheKey = CacheKey(tenantId, jurisdiction);
        UpdateFence(cacheKey, fence => new Fence(
            fence.Epoch + 1,
            Absent: true,
            fence.ReadOrdinal,
            fence.Version + 1));
        _cache.TryRemove(cacheKey, out _);
        _lastObserved.TryRemove(tenantId, out _);
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

        var cacheKey = CacheKey(tenantId, jurisdiction);

        // A read ordinal is issued in start order, not install order. The fence epoch changes only
        // on invalidation or healthy absence. The newest-starting read wins even when an older one
        // completes first, and a delayed older read cannot overwrite it.
        var (epoch, generation) = NextRead(cacheKey);

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
            // A healthy no-pack answer publishes an absence at this read's generation. It clears a
            // cache an older read installed, but cannot clear a newer pack another read published.
            PublishAbsence(tenantId, cacheKey, epoch, generation);
            return new PolicyAuthoritativeResult { Kind = PolicyAuthoritativeKind.Absent };
        }

        if (pack is null
            && _cache.TryGetValue(cacheKey, out var cached)
            && IsLive(cacheKey, cached))
        {
            var age = now - cached.ObservedAt;
            if (age <= TimeSpan.FromMinutes(5))
            {
                pack = cached.Pack;
                ruleSet = cached.RuleSet;
                packFromCache = true;
                cacheAge = age;
                _cache.TryUpdate(cacheKey, cached with { LastAccess = now }, cached);
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

        if (evaluation.Refused)
        {
            // An invalid or empty rule set cannot name a deciding rule. Returning it as a Pack
            // verdict would hand the queue a Review with no provenance, so it is a named fallback.
            return Fallback(now, RuleSetUnavailable,
                $"The pack could not produce a replayable decision: {evaluation.DecidingReason}");
        }

        if (!packFromCache)
        {
            Install(tenantId, cacheKey, epoch, generation, pack, ruleSet, now);
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
        long epoch,
        long generation,
        PolicyPack pack,
        PolicyRuleSet ruleSet,
        DateTimeOffset now)
    {
        var fence = _fences.GetOrAdd(cacheKey, _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
        if (fence.Epoch != epoch)
        {
            return;
        }

        // Publish the immutable entry atomically. The update factory keeps the higher read ordinal
        // if another read already published. A successful read at the current epoch clears the
        // Absent tombstone: absence is per-epoch, not a permanent tenant state.
        var candidate = new CacheEntry(pack, ruleSet, now, now, epoch, generation);
        _cache.AddOrUpdate(
            cacheKey,
            candidate,
            (_, existing) => generation > existing.ReadGeneration ? candidate : existing);

        var cleared = UpdateFenceIf(
            cacheKey,
            current => current.Epoch == epoch,
            current => new Fence(
                current.Epoch,
                Absent: false,
                current.ReadOrdinal,
                current.Version + 1));

        if (cleared is null || cleared.Epoch != epoch || cleared.Absent)
        {
            // A newer epoch won while this read was publishing; the stale entry is not live.
            ((ICollection<KeyValuePair<string, CacheEntry>>)_cache).Remove(
                new KeyValuePair<string, CacheEntry>(cacheKey, candidate));
            return;
        }

        while (_cache.Count > CacheCapacity)
        {
            var oldest = _cache
                .OrderBy(entry => entry.Value.LastAccess)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(oldest.Key)
                || !_cache.TryRemove(oldest.Key, out _))
            {
                break;
            }
        }

        _lastObserved[tenantId] = now;
    }

    private void PublishAbsence(
        string tenantId,
        string cacheKey,
        long epoch,
        long generation)
    {
        var observed = _fences.GetOrAdd(cacheKey, _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
        if (observed.Epoch != epoch || observed.ReadOrdinal != generation)
        {
            // A newer read has started; an older absence may not clear what it may publish.
            return;
        }

        if (!_fences.TryUpdate(
                cacheKey,
                new Fence(
                    epoch + 1,
                    Absent: true,
                    observed.ReadOrdinal,
                    observed.Version + 1),
                observed))
        {
            // Any newer fence mutation, including a same-epoch install, invalidates this absence.
            return;
        }

        _cache.TryRemove(cacheKey, out _);
        _lastObserved.TryRemove(tenantId, out _);
    }

    private (long Epoch, long Generation) NextRead(string cacheKey)
    {
        while (true)
        {
            var current = _fences.GetOrAdd(
                cacheKey,
                _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
            var next = current with
            {
                ReadOrdinal = current.ReadOrdinal + 1,
                Version = current.Version + 1,
            };

            if (_fences.TryUpdate(cacheKey, next, current))
            {
                _sequence.AddOrUpdate(cacheKey, next.ReadOrdinal, (_, _) => next.ReadOrdinal);
                return (next.Epoch, next.ReadOrdinal);
            }
        }
    }

    private Fence? UpdateFenceIf(
        string cacheKey,
        Func<Fence, bool> predicate,
        Func<Fence, Fence> update)
    {
        while (true)
        {
            var current = _fences.GetOrAdd(
                cacheKey,
                _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
            if (!predicate(current))
            {
                return null;
            }

            var next = update(current);
            if (_fences.TryUpdate(cacheKey, next, current))
            {
                return next;
            }
        }
    }

    private void UpdateFence(string cacheKey, Func<Fence, Fence> update)
    {
        while (true)
        {
            var current = _fences.GetOrAdd(
                cacheKey,
                _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
            if (_fences.TryUpdate(cacheKey, update(current), current))
            {
                return;
            }
        }
    }

    private bool IsLive(string cacheKey, CacheEntry entry)
    {
        var fence = _fences.GetOrAdd(
            cacheKey,
            _ => new Fence(0, Absent: false, ReadOrdinal: 0, Version: 0));
        return !fence.Absent && fence.Epoch == entry.Epoch;
    }

    private static string CacheKey(string tenantId, string jurisdiction) => $"{tenantId}|{jurisdiction}";

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
        DateTimeOffset ObservedAt,
        DateTimeOffset LastAccess,
        long Epoch,
        long ReadGeneration);

    private sealed record Fence(
        long Epoch,
        bool Absent,
        long ReadOrdinal,
        long Version);
}
