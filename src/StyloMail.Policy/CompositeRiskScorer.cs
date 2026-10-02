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
/// <b>A question that was never asked leaves the denominator, semantic and deterministic alike
/// (decision 42, extended uniformly by ruling (i)).</b> A MIME finding about links is asked only of a
/// message with links, and a dimension the provider reports as <c>NotApplicable</c> was never put to
/// it, so both are absent from this message's question set rather than left unanswered within it. An
/// <em>absent</em> row is the other case and stays, counting against coverage, because there the
/// question was asked and not answered. The denominator therefore falls to whatever was actually
/// askable, with <b>no floor beneath it</b>.
/// </para>
///
/// <para>
/// <b>What the denominator was holding, beyond the floor, is the irreversible cap, and that is
/// gone too.</b> The semantic backbone's fixed place in the denominator also capped coverage below
/// <c>MinimumCoverageForIrreversibleAction</c> for a deployment whose semantic rows never arrive, so
/// such a deployment could never quarantine and every high-index message held for review instead.
/// Removing the backbone removes the cap: coverage now rises to whatever the applicable
/// deterministic questions cover, and a deployment that declares it never asks can clear the
/// irreversible threshold and quarantine on deterministic evidence alone. Measured on a real host
/// over one fixture, identical in both runs at index 0.806: <c>Hold</c> before this change, with 30%
/// of dimension weight covered, and <c>Quarantine</c> after it. The surviving bound on <em>Allow</em>
/// is the corroboration gate in the engine, which refuses to act on a low index until a
/// deterministic row was measured at all. <b>That gate says nothing about the irreversible path</b>,
/// so it bounds half the engine and is not offered here as the replacement for what was removed.
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
            // one statement. Verified unreachable for THIS DIMENSION only: no POLICY-EXCLUDED row
            // has been observed at ReducedCoverage, so Available is the one availability that
            // carries an answer here. AND THE PARENTHETICAL THAT USED TO CARRY THAT CLAIM IS FALSE,
            // corrected 2026-10-02: it read "the providers use Available, Unavailable and
            // NotApplicable only", and all six decision documents of `run-benign-full-20261001T232851Z`
            // carry `deterministic.analysis_coverage` at `ReducedCoverage` (value 2, sourceVersion
            // `stylomail-mime/1`). So a reduced-coverage row DOES carry a value, and the scoped claim
            // above now rests on the observed absence of one in the excluded set rather than on a
            // provider census that was never true. That row is not weighted in that run (INFERRED
            // from `coveredWeightFraction` = 1 in the same document, which could not hold if a
            // reduced-coverage row sat in the denominator), so the index does not move today. If any
            // excluded dimension is ever assigned ReducedCoverage, the ternary below returns a null
            // reason for a row that carries an answer, which is the console blank this field exists
            // to prevent.
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

            // Applicability, applied uniformly by ruling (i). A question that was never asked is not
            // part of this message's question set, so it leaves the denominator as well as the
            // numerator rather than sitting in it as a permanent shortfall. Two shapes reach this: a
            // deterministic finding whose feature the message does not have (no links, so no question
            // about link labels), and any row a provider reports as NotApplicable, semantic included.
            // An ABSENT row is a different fact and is deliberately not this branch: the question was
            // asked and not answered, so it stays in the denominator and counts against coverage,
            // which is why the deterministic case tests the id while the reported case tests the
            // availability.
            //
            // The bound that used to be claimed here is GONE, and that is the ruling rather than an
            // oversight. The semantic backbone is no longer never-removed: a never-asking
            // deployment's semantic rows all leave, so the denominator can fall to the applicable
            // deterministic weight, a feature-poor message can clear the coverage floor on its own
            // rows, and the same removal lifts the cap that kept such a deployment below the
            // irreversible-action floor, which is measured rather than reasoned: index 0.806, Hold
            // with 30% coverage before, Quarantine after, on deterministic rows alone. The bound that
            // survives is the corroboration gate in MailPolicyEngine.DecideByRisk, and it is a bound
            // on the low-index allow path ONLY. It says nothing about the irreversible path, so it is
            // not a replacement for what was removed and must not be read as one.
            //
            // Nothing leaves this branch without being listed: both shapes append to Masked before
            // continuing, so a row that stops counting is still published, with the availability as
            // the whole explanation and no policy-exclusion reason attached.
            var neverAsked = match is null
                ? DeterministicFindings.IsDeterministic(signalId)
                : match.Availability == EvidenceAvailability.NotApplicable;

            if (neverAsked)
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
