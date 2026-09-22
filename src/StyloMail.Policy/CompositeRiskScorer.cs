using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>Outcome of combining independent semantic dimensions into one risk index.</summary>
public sealed record RiskIndexResult
{
    /// <summary>
    /// Weighted risk index over the dimensions that were actually available, 0..1.
    /// <b>A documented index, not a calibrated probability.</b>
    /// </summary>
    public required double Index { get; init; }

    /// <summary>
    /// Fraction of configured weight that was actually covered, 0..1.
    /// </summary>
    /// <remarks>
    /// Exposed because <see cref="Index"/> alone is misleading during an outage: renormalising
    /// over the surviving dimensions produces a confident-looking number from a fraction of the
    /// evidence. Policy uses this to refuse irreversible actions on thin coverage.
    /// </remarks>
    public required double CoveredWeightFraction { get; init; }

    /// <summary>Dimensions that could not contribute, with the reason. Never silently dropped.</summary>
    public required IReadOnlyList<MaskedDimension> Masked { get; init; }

    /// <summary>Signal ids that contributed, in descending weight order, for the decision ledger.</summary>
    public required IReadOnlyList<string> ContributingSignalIds { get; init; }
}

/// <summary>A dimension that contributed nothing, and why.</summary>
public sealed record MaskedDimension
{
    public required string SignalId { get; init; }

    public required EvidenceAvailability Availability { get; init; }
}

/// <summary>
/// Combines independent semantic dimensions into a single risk index with explicit weights.
/// </summary>
/// <remarks>
/// <b>Dimension probabilities are combined as a weighted sum, never multiplied.</b> The dimensions
/// are correlated — a credential-request lure usually also carries urgency — so treating them as
/// independent likelihoods and multiplying would compound one underlying signal into false
/// certainty. TypeSafe's own composite-scoring guidance covers weighted sums and is silent on
/// correlation, so the no-multiplication rule is this project's own policy decision, not a
/// vendor recommendation.
///
/// <para>
/// <b>Missing dimensions are masked, never zero-filled.</b> Treating an unavailable dimension as
/// 0.0 would fabricate a calm message out of a provider outage.
/// </para>
/// </remarks>
public static class CompositeRiskScorer
{
    /// <summary>
    /// Weighted mean over available dimensions, renormalised by the weight actually covered.
    /// </summary>
    /// <param name="evidence">Semantic evidence for one message.</param>
    /// <param name="weights">Weight per signal id. Unknown signal ids are ignored.</param>
    public static RiskIndexResult Compute(
        IEnumerable<Evidence> evidence,
        IReadOnlyDictionary<string, double> weights)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(weights);

        var contributing = new List<string>();
        var masked = new List<MaskedDimension>();
        var weighted = 0.0;
        var coveredWeight = 0.0;
        double totalWeight = 0.0;

        foreach (var (signalId, weight) in weights)
        {
            if (weight <= 0)
            {
                continue;
            }

            totalWeight += weight;

            var match = evidence.FirstOrDefault(e =>
                string.Equals(e.SignalId, signalId, StringComparison.Ordinal));

            if (match is null)
            {
                // Absent entirely — treated exactly like unavailable, not like a zero.
                masked.Add(new MaskedDimension
                {
                    SignalId = signalId,
                    Availability = EvidenceAvailability.Unavailable,
                });
                continue;
            }

            if (match.Availability != EvidenceAvailability.Available || match.Value is not { } value)
            {
                masked.Add(new MaskedDimension { SignalId = signalId, Availability = match.Availability });
                continue;
            }

            weighted += Math.Clamp(value, 0.0, 1.0) * weight;
            coveredWeight += weight;
            contributing.Add(signalId);
        }

        var index = coveredWeight > 0 ? weighted / coveredWeight : 0.0;
        var coveredFraction = totalWeight > 0 ? coveredWeight / totalWeight : 0.0;

        return new RiskIndexResult
        {
            Index = index,
            CoveredWeightFraction = coveredFraction,
            Masked = masked,
            ContributingSignalIds = contributing
                .OrderByDescending(id => weights[id])
                .ToList(),
        };
    }
}
