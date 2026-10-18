using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>The fixed v1 property keys a rule may require.</summary>
public static class PolicyPropertyKeys
{
    public const string AgeBand = "age_band";
    public const string Country = "country";
    public const string Role = "role";
    public const string Language = "language";

    public static readonly IReadOnlyList<string> All = [AgeBand, Country, Role, Language];

    public static bool IsKnown(string? key) =>
        key is not null && All.Contains(key, StringComparer.Ordinal);
}

/// <summary>The entity scope chain a policy property read addresses.</summary>
public sealed record PolicyPropertyScope(
    string? EntityId,
    string? Domain = null,
    string? Company = null,
    string? ConversationId = null);

/// <summary>One effective-property read, including its named absence.</summary>
public sealed record PolicyPropertyReadResult
{
    public required IReadOnlyList<PolicyPropertyFact> Facts { get; init; }

    /// <summary>Keys that could not be read at all. A healthy Unknown is not in this list.</summary>
    public required IReadOnlyList<string> MissingKeys { get; init; }

    /// <summary>True when the read failed rather than returned a healthy absence.</summary>
    public required bool Unavailable { get; init; }
}

/// <summary>Read-only property port. The Host adapts it to the Profiles store.</summary>
public interface IPolicyPropertyReader
{
    Task<PolicyPropertyReadResult> ReadAsync(
        string tenantId,
        PolicyPropertyScope scope,
        IReadOnlyList<string> keys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only binding port. The Host adapts it to the landed binding store.</summary>
public interface IPolicyBindingReader
{
    Task<IReadOnlyList<PolicyBinding>> ListForResolutionAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>The resolved policy input an authoritative evaluation actually used.</summary>
public sealed record EffectivePolicy
{
    public required PolicyPack Pack { get; init; }

    public required PolicyRuleSet RuleSet { get; init; }

    public required IReadOnlyDictionary<string, ToleranceLevel> Levels { get; init; }

    public required PolicyVerdict Ceiling { get; init; }

    public required IReadOnlyList<PolicyPropertyFact> PropertyFacts { get; init; }

    public required IReadOnlyList<string> MissingPropertyKeys { get; init; }

    public required IReadOnlyList<string> ForcedReviewCategories { get; init; }

    /// <summary>
    /// Categories held for review by an expired binding rather than by property uncertainty. This is
    /// deliberately separate: a binding expiry withholds even below the band, while a forced property
    /// category only raises a candidate that actually fired.
    /// </summary>
    public required IReadOnlyList<string> WithheldReviewCategories { get; init; }

    public required IReadOnlyList<PolicyBindingSnapshot> Bindings { get; init; }

    public required IReadOnlyList<string> AddressedChain { get; init; }

    public string ResolverVersion { get; init; } = EffectivePolicyResolver.Version;

    public PolicyEffectiveInputSnapshot ToSnapshot()
    {
        var levels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (category, level) in Levels)
        {
            levels[category] = level.ToString();
        }

        var forced = ForcedReviewCategories.Order(StringComparer.Ordinal).ToArray();
        var withheld = WithheldReviewCategories.Order(StringComparer.Ordinal).ToArray();
        var missing = MissingPropertyKeys.Order(StringComparer.Ordinal).ToArray();
        var facts = PropertyFacts.OrderBy(fact => fact.Key, StringComparer.Ordinal).ToArray();
        var bindings = Bindings.OrderBy(binding => binding.BindingId, StringComparer.Ordinal).ToArray();
        var chain = AddressedChain.ToArray();

        return new PolicyEffectiveInputSnapshot
        {
            ResolverVersion = ResolverVersion,
            Levels = levels,
            Ceiling = Ceiling.ToString(),
            ForcedReviewCategories = forced,
            WithheldReviewCategories = withheld,
            PropertyFacts = facts,
            MissingPropertyKeys = missing,
            AddressedChain = chain,
            Bindings = bindings,
            Digest = Digest(levels, Ceiling, forced, withheld, facts, missing, chain, bindings, ResolverVersion),
        };
    }

    private static string Digest(
        IReadOnlyDictionary<string, string> levels,
        PolicyVerdict ceiling,
        IReadOnlyList<string> forced,
        IReadOnlyList<string> withheld,
        IReadOnlyList<PolicyPropertyFact> facts,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> chain,
        IReadOnlyList<PolicyBindingSnapshot> bindings,
        string resolverVersion)
    {
        var builder = new StringBuilder();
        builder.Append("resolver=").Append(resolverVersion).Append('\n');
        builder.Append("ceiling=").Append(ceiling).Append('\n');
        foreach (var (category, level) in levels)
        {
            builder.Append("level=").Append(category).Append('=').Append(level).Append('\n');
        }

        foreach (var category in forced)
        {
            builder.Append("forced=").Append(category).Append('\n');
        }

        foreach (var category in withheld)
        {
            builder.Append("withheld=").Append(category).Append('\n');
        }

        foreach (var fact in facts)
        {
            builder.Append("fact=").Append(fact.Key).Append('|').Append(fact.Value ?? "-")
                .Append('|').Append(fact.State).Append('|').Append(fact.Source ?? "-")
                .Append('|').Append(fact.Scope ?? "-").Append('|')
                .Append(fact.Availability ?? "-").Append('|')
                .Append(fact.AvailabilityReason ?? "-").Append('\n');
        }

        foreach (var key in missing)
        {
            builder.Append("missing=").Append(key).Append('\n');
        }

        foreach (var scope in chain)
        {
            builder.Append("chain=").Append(scope).Append('\n');
        }

        foreach (var binding in bindings)
        {
            builder.Append("binding=").Append(binding.BindingId).Append('|')
                .Append(binding.Action).Append('|').Append(binding.CategoryId ?? "-")
                .Append('|').Append(binding.TargetLevel ?? "-").Append('|')
                .Append(binding.ScopeKind).Append('|').Append(binding.ScopeValue).Append('|')
                .Append(binding.StartsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append('|').Append(binding.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append('|').Append(binding.OnExpiry)
                .Append('|').Append(binding.Selected ? "1" : "0").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>
/// Resolves pack levels, property floors and bindings into one effective policy.
/// </summary>
/// <remarks>
/// Floors are reapplied after every binding target, so a less protective binding cannot erase a
/// hard floor or a property-derived protection. Binding selection is deterministic: more protective
/// target, then narrower scope, then latest start, then binding id.
/// </remarks>
public static class EffectivePolicyResolver
{
    public const string Version = "effective-policy/1";

    public static EffectivePolicy Resolve(
        PolicyPack pack,
        PolicyRuleSet ruleSet,
        IReadOnlyList<PolicyBinding> bindings,
        IReadOnlyList<PolicyPropertyFact> facts,
        IReadOnlyList<string> missingPropertyKeys,
        IReadOnlyList<string> addressedChain,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(missingPropertyKeys);

        var levels = new Dictionary<string, ToleranceLevel>(StringComparer.Ordinal);
        foreach (var (category, level) in pack.CategoryLevels)
        {
            levels[category] = level;
        }

        var forced = new HashSet<string>(StringComparer.Ordinal);
        var withheld = new HashSet<string>(StringComparer.Ordinal);
        var propertyFloors = PropertyFloors(pack, facts, forced);
        ApplyFloors(levels, propertyFloors);

        var snapshots = new List<PolicyBindingSnapshot>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<PolicyBinding>();
        var rejected = new List<PolicyBinding>();

        foreach (var binding in bindings)
        {
            // A binding applies only in its own tenant, against its own pack version and in its
            // own scope. A binding from another scope or pack is not evidence about this decision.
            if (!string.Equals(binding.TenantId, pack.TenantId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(binding.PackVersion, pack.Version, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(binding.PackId, pack.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsInScope(binding, addressedChain))
            {
                continue;
            }

            var active = binding.IsActiveAt(now);
            var heldExpiry = !active
                && binding.OnExpiry == BindingOnExpiry.HoldForReview
                && now >= binding.ExpiresAt;

            if (!active && !heldExpiry)
            {
                // A reverted binding is still part of the resolution record: it was considered,
                // and the audit must be able to say it was rejected rather than absent.
                rejected.Add(binding);
                continue;
            }

            candidates.Add(binding);

            if (heldExpiry)
            {
                if (!string.IsNullOrWhiteSpace(binding.CategoryId))
                {
                    // The category is both forced and withheld: the forced set is the replay record
                    // the effective-input snapshot carries, and the withheld set is the evaluator
                    // semantics that continues withholding below the band.
                    forced.Add(binding.CategoryId);
                    withheld.Add(binding.CategoryId);
                }
                else
                {
                    // "The scope is withheld" when the expired binding names no category: a permit
                    // or capture exception cannot be allowed to vanish at its expiry.
                    foreach (var category in PolicyCategories.All)
                    {
                        forced.Add(category.Id);
                        withheld.Add(category.Id);
                    }
                }

                selected.Add(binding.Id);
            }
        }

        // One target per category, chosen deterministically: more protective target, then narrower
        // scope, then latest start, then id. A single lower binding still applies; a raise beats a
        // lower when both cover the same category. Floors are reapplied below either way.
        var targets = candidates
            .Where(binding => binding.IsActiveAt(now)
                && binding.Action is BindingAction.RaiseTolerance or BindingAction.LowerTolerance
                && !string.IsNullOrWhiteSpace(binding.CategoryId)
                && binding.TargetLevel is not null
                && levels.ContainsKey(binding.CategoryId))
            .GroupBy(binding => binding.CategoryId!, StringComparer.Ordinal);

        foreach (var group in targets)
        {
            var chosen = group
                .OrderByDescending(binding => binding.TargetLevel)
                .ThenByDescending(binding => (int)binding.ScopeKind)
                .ThenByDescending(binding => binding.StartsAt)
                .ThenBy(binding => binding.Id, StringComparer.Ordinal)
                .First();

            levels[group.Key] = chosen.TargetLevel!.Value;
            selected.Add(chosen.Id);
        }

        foreach (var binding in candidates.Concat(rejected))
        {
            snapshots.Add(new PolicyBindingSnapshot
            {
                BindingId = binding.Id,
                Action = binding.Action.ToString(),
                CategoryId = binding.CategoryId,
                TargetLevel = binding.TargetLevel?.ToString(),
                ScopeKind = binding.ScopeKind.ToString(),
                ScopeValue = binding.ScopeValue,
                StartsAt = binding.StartsAt,
                ExpiresAt = binding.ExpiresAt,
                OnExpiry = binding.OnExpiry.ToString(),
                Selected = selected.Contains(binding.Id),
            });
        }

        // Reapply hard and property floors after every binding target. A binding may raise a
        // category, but it may never erase a hard floor or a minor/unknown protection.
        ApplyHardFloors(levels);
        ApplyFloors(levels, propertyFloors);

        return new EffectivePolicy
        {
            Pack = pack,
            RuleSet = ruleSet,
            Levels = levels,
            Ceiling = pack.ActionCeiling,
            PropertyFacts = facts,
            MissingPropertyKeys = [.. missingPropertyKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ForcedReviewCategories = [.. forced.Order(StringComparer.Ordinal)],
            WithheldReviewCategories = [.. withheld.Order(StringComparer.Ordinal)],
            Bindings = snapshots,
            AddressedChain = addressedChain,
        };
    }

    private static bool IsInScope(PolicyBinding binding, IReadOnlyList<string> addressedChain)
    {
        if (binding.ScopeKind == BindingScope.Tenant)
        {
            return true;
        }

        var key = $"{binding.ScopeKind.ToString().ToLowerInvariant()}:{binding.ScopeValue}";
        return addressedChain.Any(scope => string.Equals(scope, key, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, ToleranceLevel> PropertyFloors(
        PolicyPack pack,
        IReadOnlyList<PolicyPropertyFact> facts,
        HashSet<string> forced)
    {
        var floors = new Dictionary<string, ToleranceLevel>(StringComparer.Ordinal);
        var age = facts.FirstOrDefault(fact => fact.Key == PolicyPropertyKeys.AgeBand);
        var country = facts.FirstOrDefault(fact => fact.Key == PolicyPropertyKeys.Country);

        if (age is not null)
        {
            if (age.Value is "under_13" or "13_15" or "16_17")
            {
                Raise(floors, PolicyCategories.AdultContent, ToleranceLevel.Strict);
                Raise(floors, PolicyCategories.CoercionAndExtortion, ToleranceLevel.Strict);
                Raise(floors, PolicyCategories.Harassment, ToleranceLevel.Standard);
            }
            else if (string.Equals(age.State, "Unknown", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(age.Value)
                || string.Equals(age.Value, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                // A healthy Unknown is a measured answer with Value=null. The categories that
                // depend on age are raised to the declared protection and reviewed rather than
                // guessed; no level is lowered.
                Raise(floors, PolicyCategories.AdultContent, ToleranceLevel.Strict);
                Raise(floors, PolicyCategories.CoercionAndExtortion, ToleranceLevel.Strict);
                Raise(floors, PolicyCategories.Harassment, ToleranceLevel.Standard);
                forced.Add(PolicyCategories.AdultContent);
                forced.Add(PolicyCategories.CoercionAndExtortion);
                forced.Add(PolicyCategories.Harassment);
            }
        }

        if (country?.Value is { Length: > 0 } countryCode
            && !string.Equals(countryCode, pack.Context.Jurisdiction, StringComparison.OrdinalIgnoreCase))
        {
            // A different known country raises protection; it does not select a different pack in v1.
            forced.Add(PolicyCategories.IllegalContent);
            forced.Add(PolicyCategories.AdultContent);
        }

        return floors;
    }

    private static void Raise(
        Dictionary<string, ToleranceLevel> floors,
        string category,
        ToleranceLevel level)
    {
        if (!floors.TryGetValue(category, out var existing) || level > existing)
        {
            floors[category] = level;
        }
    }

    private static void ApplyFloors(
        Dictionary<string, ToleranceLevel> levels,
        IReadOnlyDictionary<string, ToleranceLevel> floors)
    {
        foreach (var (category, floor) in floors)
        {
            if (levels.TryGetValue(category, out var current) && floor > current)
            {
                levels[category] = floor;
            }
        }
    }

    private static void ApplyHardFloors(Dictionary<string, ToleranceLevel> levels)
    {
        foreach (var category in PolicyCategories.All.Where(category => category.IsHardFloor))
        {
            if (levels.TryGetValue(category.Id, out var level)
                && level < PolicyCategories.HardFloorMinimum)
            {
                levels[category.Id] = PolicyCategories.HardFloorMinimum;
            }
        }
    }
}
