using StyloMail.Adaptive.Profiles;
using StyloMail.Core;

namespace StyloMail.Assessment;

/// <summary>
/// Turns a list of semantic evidence into the adaptive engine's vector type.
/// </summary>
/// <remarks>
/// The conversion is where the "unknown is not zero" rule is either kept or lost, so it is done in
/// exactly one place. A dimension that was produced becomes a sample carrying its value; a
/// dimension that was not becomes a <em>missing</em> sample carrying the reason, which
/// <see cref="DimensionVector"/> refuses to represent with a numeric value at all.
///
/// <para>
/// Duplicate signal ids keep their first occurrence. The classifier answers each question once, so
/// a duplicate means a caller concatenated two evidence lists; taking the first is arbitrary but
/// deterministic, and it is better than throwing in the middle of a live message path.
/// </para>
/// </remarks>
public static class EvidenceVectors
{
    /// <summary>
    /// Builds a vector from the semantic evidence in <paramref name="evidence"/>.
    /// </summary>
    /// <param name="evidence">Evidence of any origin; only semantic items are considered.</param>
    /// <param name="expectedDimensionIds">
    /// Dimensions that should be present. Any that are absent from <paramref name="evidence"/>
    /// entirely are recorded as unavailable rather than omitted, an omitted dimension is
    /// indistinguishable from one that does not exist, and coverage would then overstate itself.
    /// </param>
    public static DimensionVector Semantic(
        IReadOnlyList<Evidence> evidence,
        IReadOnlyList<string>? expectedDimensionIds = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var samples = new List<DimensionSample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in evidence)
        {
            if (item.Origin != EvidenceOrigin.Semantic || !seen.Add(item.SignalId))
            {
                continue;
            }

            samples.Add(Sample(item));
        }

        foreach (var dimensionId in expectedDimensionIds ?? [])
        {
            if (!seen.Add(dimensionId))
            {
                continue;
            }

            samples.Add(DimensionSample.Missing(dimensionId, EvidenceAvailability.Unavailable));
        }

        return DimensionVector.Create([.. samples]);
    }

    private static DimensionSample Sample(Evidence item) => item switch
    {
        { Availability: EvidenceAvailability.Available, Value: { } value } =>
            DimensionSample.Available(item.SignalId, value),

        { Availability: EvidenceAvailability.ReducedCoverage, Value: { } value } =>
            DimensionSample.Reduced(item.SignalId, value, "reduced input coverage"),

        // An "available" signal with no value is a contradiction. Recording it as unavailable is
        // the only safe reading: there is no number to carry, and inventing one is precisely the
        // fabrication the availability model exists to prevent.
        { Availability: EvidenceAvailability.Available } =>
            DimensionSample.Missing(item.SignalId, EvidenceAvailability.Unavailable),

        { Availability: EvidenceAvailability.ReducedCoverage } =>
            DimensionSample.Missing(item.SignalId, EvidenceAvailability.Unavailable),

        { Availability: EvidenceAvailability.NotApplicable } =>
            DimensionSample.Missing(item.SignalId, EvidenceAvailability.NotApplicable),

        _ => DimensionSample.Missing(item.SignalId, EvidenceAvailability.Unavailable),
    };
}
