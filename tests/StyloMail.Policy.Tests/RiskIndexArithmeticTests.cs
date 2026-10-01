using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The arithmetic a published index has to be checkable against: what the denominator is, and which
/// rows are in it.
/// </summary>
/// <remarks>
/// <para>
/// These are not tests of the index's value, they are tests of the <b>statement the scorer makes
/// about it</b>. Decision 37 rules that a response carries the inputs to its own arithmetic, and the
/// inputs are only sufficient if the denominator is the sum of the counted rows' weights and a
/// masked row is a different fact from a measured zero. Both halves of that were false in the served
/// response until the fields existed, and neither is visible from a test that only asserts on the
/// index itself.
/// </para>
/// <para>
/// The weights are supplied here rather than read from <c>PolicyOptions</c>, so a configuration
/// change cannot make these tests pass or fail for the wrong reason.
/// </para>
/// </remarks>
public sealed class RiskIndexArithmeticTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Evidence Scored(
        string signalId,
        double value,
        EvidenceAvailability availability,
        EvidenceOrigin origin = EvidenceOrigin.Semantic) => new()
    {
        SignalId = signalId,
        Origin = origin,
        Availability = availability,
        Value = availability == EvidenceAvailability.Available ? value : null,
        Confidence = null,
        SourceVersion = "scorer-arithmetic-tests/1",
        ObservedAt = Now,
    };

    private static double IndexOf(IReadOnlyDictionary<string, double> weights, params Evidence[] evidence)
        => CompositeRiskScorer.Compute(evidence, weights).Index;

    /// <summary>
    /// The denominator the index was actually divided by, recovered from the two published figures.
    /// <c>CoveredWeightFraction</c> is <c>covered / total</c>, so this is <c>total</c>, which is the
    /// quantity decision 42's backbone bound is a statement about and which is otherwise not
    /// published. Asserted here rather than guessed at.
    /// </summary>
    private static double DenominatorOf(RiskIndexResult result)
        => result.CoveredWeight / result.CoveredWeightFraction;

    [Fact]
    public void The_denominator_is_the_sum_of_the_counted_weights()
    {
        var weights = new Dictionary<string, double> { ["semantic.a"] = 0.5, ["semantic.b"] = 0.3 };

        var result = CompositeRiskScorer.Compute(
            [
                Scored("semantic.a", 0.8, EvidenceAvailability.Available),
                Scored("semantic.b", 0.0, EvidenceAvailability.Available),
            ],
            weights);

        // A measured zero is counted: it belongs in both the numerator and the denominator, which is
        // what makes it dilute the index rather than vanish from it. The numerator is 0.4, not 0.8.
        Assert.Equal(0.8, result.CoveredWeight, precision: 12);
        Assert.Equal(0.5, result.Index, precision: 12);
        Assert.Equal(0.4, result.Index * result.CoveredWeight, precision: 12);
    }

    [Fact]
    public void A_policy_excluded_row_is_absent_from_both_denominators_and_says_why()
    {
        var weights = new Dictionary<string, double>
        {
            [SemanticDimensions.ConversationalContinuityId] = 0.5,
            ["semantic.a"] = 0.3,
        };

        var result = CompositeRiskScorer.Compute(
            [
                // Available, and excluded by policy whichever way it answered (decision 32). Counting
                // it would add 0.5 to the denominator and nothing to the numerator, which is the
                // dilution decision 31 stopped for a B; leaving it in the denominator while taking it
                // out of the numerator is the other half, and it is why the covered weight and the
                // covered fraction both have to be checked here and not just the index.
                Scored(SemanticDimensions.ConversationalContinuityId, 0.0, EvidenceAvailability.Available),
                Scored("semantic.a", 1.0, EvidenceAvailability.Available),
            ],
            weights);

        Assert.Equal(0.3, result.CoveredWeight, precision: 12);
        Assert.Equal(1.0, result.Index, precision: 12);

        // 0.3 of 0.3, not 0.3 of 0.8. The excluded weight left the total as well, so the exclusion
        // does not leave a constant coverage shortfall that reads as a caveat and carries none.
        Assert.Equal(1.0, result.CoveredWeightFraction, precision: 12);

        var masked = Assert.Single(result.Masked);
        Assert.Equal(SemanticDimensions.ConversationalContinuityId, masked.SignalId);
        Assert.Equal(EvidenceAvailability.Available, masked.Availability);

        // The reason is the part a consumer cannot infer: availability says Available, so without
        // this string the row reads as a measured zero that was somehow not counted, or worse, as a
        // measurement failure.
        Assert.NotNull(masked.Reason);
        Assert.StartsWith("policy.dimension_excluded_pending_respecification", masked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_that_was_never_measured_is_masked_without_a_reason()
    {
        var weights = new Dictionary<string, double> { ["semantic.absent"] = 0.4, ["semantic.a"] = 0.6 };

        var result = CompositeRiskScorer.Compute(
            [Scored("semantic.a", 0.5, EvidenceAvailability.Available)],
            weights);

        Assert.Equal(0.6, result.CoveredWeight, precision: 12);
        Assert.Equal(0.5, result.Index, precision: 12);

        // Unavailable is the whole explanation, so nothing more is said. A reason here would be
        // noise, and noise is how a real reason stops being read.
        var masked = Assert.Single(result.Masked);
        Assert.Null(masked.Reason);
    }

    [Fact]
    public void The_denominator_and_the_rows_agree_on_what_was_counted()
    {
        // The check decision 37 asks a consumer to be able to make, made here against the scorer's
        // own two statements: the counted weight, and which rows carry a weight at all. If these ever
        // disagree, a response assembled from the rows would contradict the index beside it.
        var weights = new Dictionary<string, double>
        {
            [SemanticDimensions.ConversationalContinuityId] = 0.5,
            ["semantic.a"] = 0.3,
            ["semantic.b"] = 0.2,
        };

        var result = CompositeRiskScorer.Compute(
            [
                Scored(SemanticDimensions.ConversationalContinuityId, 0.0, EvidenceAvailability.Available),
                Scored("semantic.a", 0.4, EvidenceAvailability.Available),
                Scored("semantic.b", 1.0, EvidenceAvailability.Unavailable),
            ],
            weights);

        var countedWeight = weights
            .Where(pair => result.ContributingSignalIds.Contains(pair.Key))
            .Sum(pair => pair.Value);

        Assert.Equal(result.CoveredWeight, countedWeight, precision: 12);

        var excludedWeight = weights.Sum(pair => pair.Value) - countedWeight;
        Assert.Equal(excludedWeight, result.Masked.Sum(m => weights[m.SignalId]), precision: 12);
    }

    /// <summary>
    /// Decision 42's step, per signal. A count contributes its weight once, whatever its magnitude,
    /// so five homographs weigh what one does and a large count cannot inflate the index. A ratio and
    /// a flag arrive in 0..1 already and are used as they are.
    /// </summary>
    [Fact]
    public void A_count_finding_contributes_once_however_large_it_is()
    {
        foreach (var (id, unit) in DeterministicFindings.Units)
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal) { [id] = 1.0 };

            if (unit == SignalUnit.Count)
            {
                Assert.Equal(0.0, IndexOf(weights, Scored(id, 0.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic)));
                Assert.Equal(1.0, IndexOf(weights, Scored(id, 1.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic)));
                Assert.Equal(1.0, IndexOf(weights, Scored(id, 5.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic)));
            }
            else
            {
                Assert.Equal(
                    0.4,
                    IndexOf(weights, Scored(id, 0.4, EvidenceAvailability.Available, EvidenceOrigin.Deterministic)),
                    precision: 12);
            }
        }
    }

    /// <summary>
    /// The step against the arithmetic a reader is told to perform with it: for every count in the
    /// domain a count can occupy, <c>Normalise</c> and a clamp to 0..1 produce the same number, which
    /// is why a consumer dividing the served rows reproduces the scorer's figure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is one half only, and it is the half this assembly can state.</b> The other half is the
    /// reader's own clamp, and there is no product code in it: `Math.Clamp` appears in Policy and in
    /// the assessment suite's model of the served arithmetic (`MailAssessorTests.cs:409`), not in the
    /// assessor. A pin that fails when EITHER side moves has to live where both are visible, which is
    /// the Assessment suite (it references Mime and Policy), and I have asked `ingress-` for it rather
    /// than taking a project I do not own.
    /// </para>
    /// <para>
    /// The agreement rests on counts being integers and nothing enforces that: a count of 0.5 would
    /// clamp to 0.5 and step to 0.0. Stated here so the assumption is written down beside the pin that
    /// depends on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_count_step_agrees_with_a_clamp_across_the_domain_a_count_occupies()
    {
        foreach (var count in (double[])[0.0, 1.0, 2.0, 5.0, 1000.0])
        {
            Assert.Equal(
                Math.Clamp(count, 0.0, 1.0),
                DeterministicFindings.Normalise(count, SignalUnit.Count));
        }
    }

    /// <summary>
    /// Decision 42(e), the applicability rule and its scope. It reaches the deterministic findings
    /// only: a question a message cannot pose leaves the denominator with the answer, while a
    /// semantic row stays in it whatever its availability, because the semantic questions are what
    /// anchor coverage across messages.
    /// </summary>
    [Fact]
    public void An_inapplicable_deterministic_finding_leaves_the_denominator_and_an_unanswered_one_stays()
    {
        const string link = DeterministicFindings.LinkDisplayMismatch;

        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["semantic.a"] = 1.0,
            [link] = 1.0,
        };

        var answered = CompositeRiskScorer.Compute(
            [
                Scored("semantic.a", 0.0, EvidenceAvailability.Available),
                Scored(link, 0.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic),
            ],
            weights);

        var inapplicable = CompositeRiskScorer.Compute(
            [
                Scored("semantic.a", 0.0, EvidenceAvailability.Available),
                Scored(link, 0.0, EvidenceAvailability.NotApplicable, EvidenceOrigin.Deterministic),
            ],
            weights);

        var unanswered = CompositeRiskScorer.Compute(
            [
                Scored("semantic.a", 0.0, EvidenceAvailability.Available),
                Scored(link, 0.0, EvidenceAvailability.Unavailable, EvidenceOrigin.Deterministic),
            ],
            weights);

        // A message with links poses the link question, both rows are in the denominator and both
        // were answered.
        Assert.Equal(2.0, DenominatorOf(answered), precision: 12);

        // A message with no links is not asked about link labels: the question leaves the denominator
        // with the answer, so this is 1.0 rather than 2.0 and coverage is not diluted by a question
        // the message could not pose.
        Assert.Equal(1.0, DenominatorOf(inapplicable), precision: 12);
        Assert.Equal(1.0, inapplicable.CoveredWeightFraction, precision: 12);

        // Applicable and unanswered is a different fact. The question was asked and not answered, so
        // it stays in the denominator and counts against coverage.
        Assert.Equal(2.0, DenominatorOf(unanswered), precision: 12);
        Assert.Equal(0.5, unanswered.CoveredWeightFraction, precision: 12);

        // The availability is the whole explanation for an inapplicable row: no policy reason, no
        // measurement failure. "Did not apply" and "excluded by policy" never collapse into one.
        var masked = Assert.Single(inapplicable.Masked);
        Assert.Equal(link, masked.SignalId);
        Assert.Equal(EvidenceAvailability.NotApplicable, masked.Availability);
        Assert.Null(masked.Reason);

        // Unavailable is the other side of the same line, and origin does not decide it: a question
        // that was asked and not answered stays in the denominator whichever layer produced it, so
        // this is 0.0 rather than a comfortable 1.0 over an empty set. Ruling (i) gave a semantic row
        // the same exit a deterministic one has, but only for NotApplicable; absence and
        // unavailability are what stay.
        var semanticOnlyUnavailable = CompositeRiskScorer.Compute(
            [Scored("semantic.a", 0.0, EvidenceAvailability.Unavailable)],
            weights);

        Assert.Equal(0.0, semanticOnlyUnavailable.CoveredWeightFraction, precision: 12);
    }

    /// <summary>
    /// Ruling (i): a semantic question the provider reports as <b>never asked</b> leaves the
    /// denominator, exactly as a deterministic finding for a feature the message does not have
    /// already does. Written to FAIL before that change and pass after it.
    /// </summary>
    /// <remarks>
    /// The fixture is deliberately not continuity. Continuity is policy-excluded before this branch
    /// is reached, so it never arrives here and a continuity fixture would be green twice over. The
    /// id below is an ordinary non-excluded semantic dimension, so its availability is the only thing
    /// deciding whether it is counted. The two runs differ in one row and nothing else, so the
    /// difference between the denominators is that row's weight and neither assertion goes stale on a
    /// reweight.
    /// </remarks>
    [Fact]
    public void A_semantic_question_that_was_never_asked_leaves_the_denominator()
    {
        var weights = new Dictionary<string, double>
        {
            ["semantic.credential_request"] = 0.6,
            ["semantic.urgency_pressure"] = 0.4,
        };

        var answerable = CompositeRiskScorer.Compute(
            [
                Scored("semantic.urgency_pressure", 0.0, EvidenceAvailability.Available),
                Scored("semantic.credential_request", 0.0, EvidenceAvailability.Available),
            ],
            weights);

        var neverAsked = CompositeRiskScorer.Compute(
            [
                Scored("semantic.urgency_pressure", 0.0, EvidenceAvailability.Available),
                Scored("semantic.credential_request", 0.0, EvidenceAvailability.NotApplicable),
            ],
            weights);

        // Both questions were put, so both weights are in the denominator.
        Assert.Equal(1.0, DenominatorOf(answerable), precision: 12);

        // The never-asked question was not part of this message's set, so it leaves the denominator
        // as well as the numerator rather than sitting in it as a permanent shortfall.
        Assert.Equal(0.4, DenominatorOf(neverAsked), precision: 12);

        // It is still masked, and the availability is the whole explanation: "did not apply" must not
        // read as "excluded by policy".
        var masked = Assert.Single(neverAsked.Masked);
        Assert.Equal("semantic.credential_request", masked.SignalId);
        Assert.Equal(EvidenceAvailability.NotApplicable, masked.Availability);
        Assert.Null(masked.Reason);

        // The branch carries two shapes, so the publication is pinned for both. This half is the
        // deterministic one: a question the message never raised has no row at all, and the row still
        // leaves the denominator and is still published rather than silently dropped, which is what
        // `RiskIndexResult.Masked` promises. A branch that removed a row from the arithmetic without
        // listing it would make coverage fall for a reason no reader of the response could see.
        var neverRaised = CompositeRiskScorer.Compute(
            [
                Scored("semantic.urgency_pressure", 0.0, EvidenceAvailability.Available),
                Scored("semantic.credential_request", 0.0, EvidenceAvailability.Available),
            ],
            new Dictionary<string, double>
            {
                ["semantic.credential_request"] = 0.6,
                ["semantic.urgency_pressure"] = 0.4,
                [DeterministicFindings.LinkDisplayMismatch] = 0.8,
            });

        Assert.Equal(1.0, DenominatorOf(neverRaised), precision: 12);

        var listed = Assert.Single(neverRaised.Masked);
        Assert.Equal(DeterministicFindings.LinkDisplayMismatch, listed.SignalId);
        Assert.Equal(EvidenceAvailability.Unavailable, listed.Availability);
        Assert.Null(listed.Reason);
    }

    /// <summary>
    /// The denominator when every semantic question was put and answered: the backbone, 7.3, with a
    /// posed deterministic question growing it rather than diluting it.
    /// <b>This is not a floor</b>, and it was read as one until ruling (i). A semantic row reported
    /// <c>NotApplicable</c> now leaves the denominator exactly as a deterministic one for a missing
    /// feature does, so the denominator can fall below the backbone. What guards a feature-poor
    /// message from clearing the coverage <b>allow</b> floor is the engine's separate corroboration
    /// gate, not this arithmetic; the claim that belonged here is pinned there. That gate does not
    /// reach the irreversible path, so nothing in this test bounds quarantine either.
    /// </summary>
    [Fact]
    public void The_denominator_is_the_full_backbone_when_every_semantic_question_was_answered()
    {
        var weights = new PolicyOptions().DimensionWeights;

        var backbone = SemanticDimensions.All
            .Select(dimension => dimension.Id)
            .Where(id => id != SemanticDimensions.ConversationalContinuityId)
            .Select(id => Scored(id, 0.0, EvidenceAvailability.Available))
            .ToArray();

        var bare = CompositeRiskScorer.Compute(backbone, weights);

        // 7.3 and not 7.8: continuity is out of the total as well as the numerator (decision 32).
        Assert.Equal(7.3, bare.CoveredWeight, precision: 9);
        Assert.Equal(7.3, DenominatorOf(bare), precision: 9);
        Assert.Equal(1.0, bare.CoveredWeightFraction, precision: 12);

        // A message that poses one more question grows the denominator rather than shrinking it: the
        // backbone is still in it, at 7.3, beside the link question.
        var withLink = CompositeRiskScorer.Compute(
            [
                .. backbone,
                Scored(DeterministicFindings.LinkDisplayMismatch, 0.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic),
            ],
            weights);

        Assert.Equal(8.3, withLink.CoveredWeight, precision: 9);
        Assert.Equal(8.3, DenominatorOf(withLink), precision: 9);
    }

    /// <summary>
    /// Decision 42's range, as a range rather than one instance, because the window is a formula: the
    /// coverage is <c>c / T</c> and it scales with what the message asks. With every semantic question
    /// put and no deterministic one posed the denominator is the backbone, 7.3; with all ten
    /// deterministic questions posed as well it is 7.3 + 7.7 = 15.0.
    /// <b>These are the ends for a message that puts every question, not a floor.</b> Ruling (i)
    /// removed the floor reading: a question reported as never asked leaves the denominator, so a
    /// message that poses fewer questions has a smaller one, and the guard against a feature-poor
    /// message clearing the coverage <b>allow</b> floor is the engine's corroboration gate, not this
    /// arithmetic. That gate does not reach the irreversible path, where the same removal lifts the
    /// cap and a never-asking deployment can now quarantine on deterministic rows alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a message that puts every semantic question the denominator is at least the backbone: those
    /// rows add their weight whatever their availability, and continuity adds nothing in either
    /// direction (decision 32), so the semantic contribution is a constant 7.3 while every one of them
    /// is asked. Ruling (i) is the exception, and it is why this is a range and not a floor: a row
    /// reported <c>NotApplicable</c> was never asked, so it leaves, and a never-asking deployment's
    /// denominator falls to the applicable deterministic weight alone. Read against the framing in
    /// which the continuity row is askable and not counted, the askable total is this denominator plus
    /// 0.5, so <c>T</c> runs over <c>[7.8, 15.5]</c> wherever every semantic question was put.
    /// </para>
    /// <para>
    /// The eight e2e corpus arms do not exercise any of this: <c>ingress-</c> measured all eleven
    /// counted dimensions Available on all eight, so covered equals askable and coverage is 1.0
    /// against the 7.3 denominator, with nothing partially answering. The window is reachable in the
    /// arithmetic and unexercised by that corpus by construction, and this pin is what covers it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_denominator_runs_from_the_backbone_to_the_full_question_set_when_every_question_is_put()
    {
        var weights = new PolicyOptions().DimensionWeights;

        var backbone = SemanticDimensions.All
            .Where(dimension => dimension.Id != SemanticDimensions.ConversationalContinuityId)
            .Select(dimension => Scored(dimension.Id, 0.0, EvidenceAvailability.Available))
            .ToArray();

        var smallest = CompositeRiskScorer.Compute(backbone, weights);

        var largest = CompositeRiskScorer.Compute(
            backbone.Concat(DeterministicFindings.Units.Keys.Select(
                id => Scored(id, 0.0, EvidenceAvailability.Available, EvidenceOrigin.Deterministic))),
            weights);

        Assert.Equal(7.3, DenominatorOf(smallest), precision: 9);

        // 7.3 of semantic weight plus 7.7 across the ten deterministic findings: the whole question
        // set a feature-rich message poses.
        Assert.Equal(15.0, DenominatorOf(largest), precision: 9);

        // Both bounds are exactly covered, so the fractions are 1.0 and the range is about the
        // denominator rather than about an unanswered question.
        Assert.Equal(1.0, smallest.CoveredWeightFraction, precision: 12);
        Assert.Equal(1.0, largest.CoveredWeightFraction, precision: 12);
    }

    /// <summary>
    /// The three directions a deterministic question moves coverage, which is what decides whether
    /// decision 42 shifts a floor window at all, and which the case list fixes only if the cases are
    /// read correctly. A measured row adds its weight to the covered weight and to the denominator
    /// alike, so coverage rises toward 1; an askable-and-unmeasured row adds to the denominator alone,
    /// so coverage falls; an inapplicable row leaves both, so nothing moves.
    /// </summary>
    /// <remarks>
    /// The measured case does not depend on the row's value. Coverage counts the weight that was
    /// answered and not the risk in the answers, so a calm row and an objecting row of the same weight
    /// move coverage identically and differ only in the index. Written down as a pin because the first
    /// version of this test reasoned the direction from the numerator and asserted the reverse, and the
    /// arithmetic is what settled it: 2.0 / 3.0 above 0.5, not below.
    /// </remarks>
    [Fact]
    public void A_deterministic_question_moves_coverage_by_the_case_it_falls_in()
    {
        const string link = DeterministicFindings.LinkDisplayMismatch;

        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["semantic.a"] = 1.0,
            ["semantic.b"] = 1.0,
            [link] = 1.0,
        };

        var baseEvidence = new[]
        {
            Scored("semantic.a", 1.0, EvidenceAvailability.Available),
            Scored("semantic.b", 0.0, EvidenceAvailability.Unavailable),
        };

        double Coverage(EvidenceAvailability? linkAvailability) => CompositeRiskScorer.Compute(
            linkAvailability is { } availability
                ? [.. baseEvidence, Scored(link, 0.0, availability, EvidenceOrigin.Deterministic)]
                : baseEvidence,
            weights).CoveredWeightFraction;

        // One of the two semantic rows answered, so half the weight was covered.
        Assert.Equal(0.5, Coverage(null), precision: 12);

        // Measured: the weight goes to the numerator and the denominator alike, so coverage rises.
        Assert.Equal(2.0 / 3.0, Coverage(EvidenceAvailability.Available), precision: 12);
        Assert.True(Coverage(EvidenceAvailability.Available) > Coverage(null));

        // Askable and unanswered: the denominator alone, so coverage falls.
        Assert.Equal(1.0 / 3.0, Coverage(EvidenceAvailability.Unavailable), precision: 12);
        Assert.True(Coverage(EvidenceAvailability.Unavailable) < Coverage(null));

        // Inapplicable: neither, so nothing moves at all.
        Assert.Equal(Coverage(null), Coverage(EvidenceAvailability.NotApplicable), precision: 12);
    }
}
