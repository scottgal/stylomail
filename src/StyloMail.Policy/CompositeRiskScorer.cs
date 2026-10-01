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

    /// <summary>
    /// The summed weight of the dimensions that were counted: the denominator <see cref="Index"/>
    /// was divided by.
    /// </summary>
    /// <remarks>
    /// Published so an index can be <b>checked</b> rather than re-derived. The counted rows carry
    /// their own weight, and this is the number they sum to; a response that publishes an index but
    /// not its denominator leaves a consumer to guess which rows were counted, and that guess is
    /// wrong exactly when a row was excluded rather than counted (decisions 31 and 32).
    /// </remarks>
    public required double CoveredWeight { get; init; }

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

    /// <summary>
    /// Why a row was left out, when the availability alone does not say it. Null for the ordinary
    /// cases, where "absent" or "unavailable" is the whole explanation. Set for a row that was
    /// <see cref="EvidenceAvailability.Available"/> and was still not counted, so an available row
    /// is never silently absent from the arithmetic.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Combines independent semantic dimensions into a single risk index with explicit weights.
/// </summary>
/// <remarks>
/// <b>Dimension probabilities are combined as a weighted sum, never multiplied.</b> The dimensions
/// are correlated, a credential-request lure usually also carries urgency, so treating them as
/// independent likelihoods and multiplying would compound one underlying signal into false
/// certainty. TypeSafe's own composite-scoring guidance covers weighted sums and is silent on
/// correlation, so the no-multiplication rule is this project's own policy decision, not a
/// vendor recommendation.
///
/// <para>
/// <b>Missing dimensions are masked, never zero-filled.</b> Treating an unavailable dimension as
/// 0.0 would fabricate a calm message out of a provider outage.
/// </para>
///
/// <para>
/// <b>A dimension can also be out of the index by policy, and that is a different fact from being
/// unavailable.</b> An excluded dimension leaves the numerator <em>and</em> both denominators, and
/// the two must move together: an exclusion that removed only the numerator would leave a permanent
/// coverage shortfall that is a constant, and a constant carries no information while looking like
/// one. Such a row is still published in <see cref="RiskIndexResult.Masked"/>, carrying its reason
/// where it carried an answer (decision 32). An exclusion is never allowed to raise coverage while
/// lowering the index.
/// </para>
///
/// <para>
/// <b>Deterministic findings are posed only when the message has the feature they are about, and a
/// finding that is not posed leaves the denominator (decision 42).</b> A semantic question is asked
/// whenever a provider is up, so it is the backbone that keeps coverage comparable across messages
/// and it stays whatever its availability; a MIME finding about links is asked only of a message with
/// links. The bound that makes the shrinking denominator safe is that the semantic backbone is never
/// removed, so the denominator can never fall below its weight.
/// </para>
/// </remarks>
public static class CompositeRiskScorer
{
    /// <summary>
    /// Dimensions excluded from the index in <b>both</b> directions by a policy ruling rather than by
    /// their own answer. Conversational continuity is the only one today, and decision 32 is why: the
    /// axis it responds to is <em>restatement</em>, not risk, so as a risk term it charges index for
    /// the least informative message in a thread and nothing for the informative one.
    /// </summary>
    /// <remarks>
    /// This supersedes decision 31's one-sided rule <b>for this dimension only</b>. Under 31 a row
    /// that did not confirm was left out and a confirming one counted; decision 32 excludes the row
    /// whichever way it answers, which is strictly stronger. Decision 31's rule remains the general
    /// concept (a dimension whose subject is our own assembly may never dilute the index nor cover
    /// it), and any future dimension of that shape belongs here rather than in a second mechanism.
    /// </remarks>
    private static readonly string[] ExcludedByPolicyIds =
    [
        SemanticDimensions.ConversationalContinuityId,
    ];

    /// <summary>
    /// The reason a policy-excluded row carries <b>when it carries an answer at all</b>: set where
    /// the availability is <see cref="EvidenceAvailability.Available"/>, in both directions alike.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> set where the question was never askable (<c>NotApplicable</c>) or was
    /// not answered (<c>Unavailable</c>, or absent). There the availability is the whole explanation,
    /// and "this dimension was excluded by policy" is a different statement from "this question did
    /// not apply": it would tell an audit that a ruling was made about an answer that does not exist.
    /// The two must stay readable apart, which is why this is scoped rather than unconditional.
    /// </remarks>
    private const string PolicyExclusionReason =
        "policy.dimension_excluded_pending_respecification: this dimension is excluded from the index "
        + "in both directions by policy decision 32, pending respecification. It is a policy decision "
        + "an operator instruction can reverse, and not a measurement failure.";

    private static bool IsExcludedByPolicy(string signalId) =>
        ExcludedByPolicyIds.Contains(signalId, StringComparer.Ordinal);

    /// <summary>
    /// A signal's value in 0..1, by its declared unit. A deterministic finding is normalised by
    /// <see cref="DeterministicFindings.Normalise"/> (a count becomes a bounded step); anything else
    /// is a 0..1 ratio already and is clamped.
    /// </summary>
    private static double Normalised(string signalId, double value) =>
        DeterministicFindings.Units.TryGetValue(signalId, out var unit)
            ? DeterministicFindings.Normalise(value, unit)
            : Math.Clamp(value, 0.0, 1.0);

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

            var match = evidence.FirstOrDefault(e =>
                string.Equals(e.SignalId, signalId, StringComparison.Ordinal));

            // A policy-excluded dimension leaves the index entirely, the numerator and the
            // denominator alike, so the denominator is the sum of the remaining weights and never
            // includes this one (decision 32). Note the total is not accumulated here, which is the
            // half of the exclusion a numerator-only fix would miss, leaving a permanent and
            // meaningless coverage shortfall in its place.
            //
            // It is still published in Masked, and the reason is scoped to the case the ruling is
            // about: a row that carries an answer and is excluded anyway. Where the question was
            // never askable or was not answered, the availability is the operative explanation and
            // the reason stays null, so "did not apply" and "excluded by policy" never collapse into
            // one statement. Verified unreachable for this dimension: no classifier assigns it
            // ReducedCoverage (the providers use Available, Unavailable and NotApplicable only), so
            // Available is the one availability that carries an answer here.
            if (IsExcludedByPolicy(signalId))
            {
                masked.Add(new MaskedDimension
                {
                    SignalId = signalId,
                    Availability = match?.Availability ?? EvidenceAvailability.Unavailable,
                    Reason = match is { Availability: EvidenceAvailability.Available }
                        ? PolicyExclusionReason
                        : null,
                });
                continue;
            }

            // Applicability, for deterministic findings only (decision 42). A semantic question is
            // posed whenever a provider is up, so it is the backbone that anchors coverage across
            // messages and it stays in the denominator whatever its availability. A deterministic
            // finding is posed only when the message has the feature it is about: a message with no
            // links cannot be asked about link labels, so that finding is not in this message's
            // question set and leaves the denominator as well as the numerator. An applicable but
            // unanswered finding (present, Unavailable) stays and counts against coverage.
            //
            // The bound this depends on: the semantic backbone is never removed here, so the
            // denominator can never fall below its weight (7.3 today), and no feature-poor message
            // can shrink the denominator far enough to clear a floor it should not.
            if (DeterministicFindings.IsDeterministic(signalId)
                && (match is null || match.Availability == EvidenceAvailability.NotApplicable))
            {
                masked.Add(new MaskedDimension
                {
                    SignalId = signalId,
                    Availability = match?.Availability ?? EvidenceAvailability.Unavailable,
                });
                continue;
            }

            totalWeight += weight;

            if (match is null)
            {
                // Absent entirely, treated exactly like unavailable, not like a zero.
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

            weighted += Normalised(signalId, value) * weight;
            coveredWeight += weight;
            contributing.Add(signalId);
        }

        var index = coveredWeight > 0 ? weighted / coveredWeight : 0.0;
        var coveredFraction = totalWeight > 0 ? coveredWeight / totalWeight : 0.0;

        return new RiskIndexResult
        {
            Index = index,
            CoveredWeightFraction = coveredFraction,
            CoveredWeight = coveredWeight,
            Masked = masked,
            ContributingSignalIds = contributing
                .OrderByDescending(id => weights[id])
                .ToList(),
        };
    }
}
