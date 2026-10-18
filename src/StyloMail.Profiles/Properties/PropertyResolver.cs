namespace StyloMail.Profiles.Properties;

/// <summary>
/// Reduces competing property rows to the effective value for one key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence is protective, not confident.</b> Three rules, chosen by the key's definition:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>MostProtective</b> (age band): the highest protection rank wins across every scope and
/// source, because a real minor protection may never be lost to a more confident adult estimate.
/// Ties fall through to source rank, then specificity, then the most recent set.
/// </item>
/// <item>
/// <b>HumanOnly</b> (country): only the sources the definition allows may compete at all, so an
/// inferred or default country is visible in the evidence list and cannot govern. Ties fall through
/// to source rank, then specificity, then the most recent set.
/// </item>
/// <item>
/// <b>SourceOrder</b> (role, language): strongest source wins, narrower scope breaks the tie, most
/// recent set breaks the remainder.
/// </item>
/// </list>
/// <para>
/// The resolver is pure and takes the clock, so a test can state an instant instead of waiting for
/// one, and so the same rows always produce the same answer at the same instant.
/// </para>
/// </remarks>
public static class PropertyResolver
{
    /// <summary>Resolves one key over the candidate rows.</summary>
    public static EffectiveProperty Resolve(
        PropertyDefinition definition,
        IEnumerable<PropertyRecord> candidates,
        DateTimeOffset now)
    {
        var considered = new List<(PropertyRecord Record, string Normalised, bool Eligible, int Protection)>();

        foreach (var record in candidates)
        {
            if (!string.Equals(record.Key, definition.Key, StringComparison.Ordinal)
                || !record.IsLiveAt(now))
            {
                continue;
            }

            // A row that cannot satisfy the key as it is defined today is not eligible. This is
            // defence in depth against a row written by an older build: the write path validates,
            // and a value that no longer parses must never be handed to policy as if it did.
            var parses = PropertyKeys.TryNormalise(definition, record.Value, out var normalised, out _);
            var eligible = parses && PropertyKeys.AcceptsSource(definition, record.Source);

            considered.Add((
                record,
                parses ? normalised : record.Value,
                eligible,
                parses ? PropertyKeys.ProtectionRank(definition.Key, normalised) : -1));
        }

        var ordered = considered
            .OrderByDescending(entry => entry.Eligible)
            .ThenByDescending(entry => entry.Eligible ? entry.Protection : -1)
            .ThenByDescending(entry => PropertySourceRank.Of(entry.Record.Source))
            .ThenByDescending(entry => PropertyScopeKinds.Specificity(entry.Record.Scope.Kind))
            .ThenByDescending(entry => entry.Record.SetAt)
            .ToList();

        var winner = ordered.FirstOrDefault(entry => entry.Eligible);
        var candidatesView = ordered
            .Select(entry => new PropertyCandidate
            {
                Record = entry.Record,
                Selected = winner.Record is not null && ReferenceEquals(entry.Record, winner.Record),
                Eligible = entry.Eligible,
            })
            .ToList();

        if (winner.Record is null)
        {
            return EffectiveProperty.Unknown(definition.Key, candidatesView);
        }

        return new EffectiveProperty
        {
            Key = definition.Key,
            Value = winner.Normalised,
            State = PropertySurfaceStates.For(winner.Record.Source, winner.Record.ConfirmedBy),
            Source = winner.Record.Source,
            Scope = winner.Record.Scope,
            Confidence = winner.Record.Confidence,
            Model = winner.Record.Model,
            EvidenceRefs = winner.Record.EvidenceRefs,
            Coverage = winner.Record.Coverage,
            SetBy = winner.Record.SetBy,
            SetAt = winner.Record.SetAt,
            ExpiresAt = winner.Record.ExpiresAt,
            ConfirmedBy = winner.Record.ConfirmedBy,
            ConfirmedAt = winner.Record.ConfirmedAt,
            Availability = PropertyAvailability.Available,
            Candidates = candidatesView,
        };
    }

    /// <summary>Resolves every requested key, preserving the requested order.</summary>
    public static IReadOnlyList<EffectiveProperty> ResolveAll(
        IEnumerable<string> keys,
        IEnumerable<PropertyRecord> candidates,
        DateTimeOffset now)
    {
        var rows = candidates as IReadOnlyCollection<PropertyRecord> ?? candidates.ToList();

        return
        [
            .. keys.Select(key => Resolve(PropertyKeys.Require(key), rows, now)),
        ];
    }
}
