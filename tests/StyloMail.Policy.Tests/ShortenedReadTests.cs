using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// Pins the 2026-10-01 ruling on a SHORTENED READ.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state, and why it is common rather than an edge.</b> The Nimble adapter renders the body and
/// the quoted tail into the state under a shared character budget (<c>MaxBodyCharacters</c>, 2500 by
/// default), and the fit shortens further when the whole serialized request exceeds <c>NumCtx</c>
/// bytes, so a message whose body or quoted tail is long enough is assessed over SHORTENED input and
/// its answers are real answers about the message as bounded. Nothing in this suite drove that state
/// until now, which is why the fleet's greens covered everything except this path.
/// </para>
/// <para>
/// <b>TWO CORRECTIONS TO THE PARAGRAPH ABOVE, both dated 2026-10-02 and both found by other lanes
/// rather than by me.</b> First, the cut is a SIZE rule and not a density rule: at a constant 2500
/// characters every shape is shortened, prose exactly like hex-ish. Second, the input that can be
/// shortened is the body OR the quoted tail, while <c>body_text_characters_kept</c> records only the
/// BODY, so a row can report shortened with the kept length equal to the FULL uncut body, and a
/// capture cannot tell which of the two fields was cut. Neither correction changes what this class
/// pins: the row is <see cref="EvidenceAvailability.Available"/> and carries the reason, whichever
/// field was cut.
/// </para>
/// <para>
/// <b>The ruling these tests pin.</b> A shortened read stays <see cref="EvidenceAvailability.Available"/>
/// and the fact that the input was cut travels in the row's <c>reason</c> attribute. The alternative,
/// mapping it to <see cref="EvidenceAvailability.ReducedCoverage"/>, was considered and rejected: it
/// would remove the row from <c>coveredWeight</c> while leaving it in <c>totalWeight</c>, so the
/// coverage fraction would fall and the ALLOW floor (<c>MinimumCoverageForAllow</c>, 0.30) would
/// refuse ordinary long correspondence, and it would disarm
/// <c>MailPolicyEngine.HasElevatedSecuritySignal</c>, which requires <c>Available</c> and whose whole
/// job is to KEEP a hold against the recipient's preference.
/// </para>
/// <para>
/// <b>STATUS: WRITTEN AND NOT COMPILED OR RUN.</b> No build slot was taken for it, so nothing here is
/// a green claim. It is prepared by `policy-` and should be compiled and run as its own increment,
/// because a test nobody has run is a claim nobody has checked.
/// </para>
/// </remarks>
public sealed class ShortenedReadTests
{
    /// <summary>
    /// The literal the adapter writes, copied from
    /// <c>NimbleSemanticMailClassifier.PromptShortenedReason</c>. It is a literal here because that
    /// constant is private to that class; if the adapter's wording changes, this fixture stops being
    /// a state the mechanism produces, which is the one thing a fixture must be.
    /// </summary>
    /// <remarks>
    /// The copy is not a pin, and it cannot become one from here. This file is UNTRACKED on
    /// <c>main</c>, so on the committed tree the only carrier of the reason text is the Host suite
    /// (<c>AvailabilityReasonsTests.cs</c>), which states in its own scope that a reworded constant
    /// must NOT fail it because what it tests is the mapping. The assertion that would pin the text
    /// belongs at the WIRE - the served reason on the decision response - and that is the same
    /// instrument the Desktop contract tests aim at wire names.
    /// </remarks>
    private const string PromptShortenedReason =
        "the client shortened the message body to fit the context window";

    /// <summary>Two dimensions, so the arithmetic is legible and coverage is not the confound.</summary>
    private static readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal)
    {
        ["semantic.credential_request"] = 1.0,
        ["semantic.unsolicited_solicitation"] = 1.0,
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A semantic row, optionally carrying the adapter's shortening reason.</summary>
    private static Evidence Semantic(string id, double value, string? reason = null) => new()
    {
        SignalId = id,
        Origin = EvidenceOrigin.Semantic,
        Availability = EvidenceAvailability.Available,
        Value = value,
        Confidence = null,
        SourceVersion = "jev-1.13.0",
        ObservedAt = Now,
        Attributes = reason is null ? null : [new EvidenceAttribute { Name = "reason", Value = reason }],
    };

    private static PolicyContext PreferenceContext() => new()
    {
        EmergencyKillSwitchEngaged = false,
        OutboundQuotaExhausted = false,
        VerifiedSecurityRuleViolations = [],
        AllowlistEntryValid = false,
        BaselineFrozenForSuspectedCompromise = false,
        RecipientPrefersThisTrafficClass = true,
    };

    private static PolicyDecision Decide(IReadOnlyList<Evidence> evidence, PolicyOptions options)
        => new MailPolicyEngine(options).Decide(new PolicyInput
        {
            Evidence = evidence,
            Risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights),
            Context = PreferenceContext(),
            Direction = MailDirection.Inbound,
        });

    /// <summary>
    /// The reason is METADATA and not arithmetic: a row carrying it contributes exactly as the same
    /// row without it does, and the control shows the test can tell the two availabilities apart.
    /// </summary>
    [Fact]
    public void A_shortened_read_stays_counted_and_carries_its_reason()
    {
        var options = new PolicyOptions();
        var weights = options.DimensionWeights;

        var withoutReason = CompositeRiskScorer.Compute(
            [Semantic("semantic.credential_request", 0.9)], weights);
        var shortened = CompositeRiskScorer.Compute(
            [Semantic("semantic.credential_request", 0.9, PromptShortenedReason)], weights);

        // The shortened row is counted: it is a contributing signal, it is in the covered weight,
        // and both the index and the fraction are identical to the same row without the attribute.
        Assert.Contains("semantic.credential_request", shortened.ContributingSignalIds);
        Assert.Equal(withoutReason.CoveredWeight, shortened.CoveredWeight);
        Assert.Equal(withoutReason.CoveredWeightFraction, shortened.CoveredWeightFraction);
        Assert.Equal(withoutReason.Index, shortened.Index);
        Assert.DoesNotContain(
            shortened.Masked, m => m.SignalId == "semantic.credential_request");

        // CONTROL, and it is what makes the assertions above a measurement rather than a tautology:
        // the SAME row marked Unavailable does not contribute, does not enter the covered weight, and
        // therefore drags the fraction down. The two availabilities are distinguishable here, so the
        // equality above is a fact about the row's availability and not about the instrument.
        var unavailable = CompositeRiskScorer.Compute(
            [Semantic("semantic.credential_request", 0.9) with
            {
                Availability = EvidenceAvailability.Unavailable,
                Attributes = [new EvidenceAttribute { Name = "reason", Value = PromptShortenedReason }],
            }],
            weights);

        Assert.DoesNotContain("semantic.credential_request", unavailable.ContributingSignalIds);
        Assert.Contains(unavailable.Masked, m => m.SignalId == "semantic.credential_request");
        Assert.True(
            unavailable.CoveredWeightFraction < shortened.CoveredWeightFraction,
            "An Unavailable row must lower the coverage fraction; if it does not, the control is dead "
            + "and the equality in this test was never measuring the availability.");
    }

    /// <summary>
    /// And the security gate stays ARMED on a shortened read, which is the half of the ruling that
    /// keeps a message the recipient's preference would otherwise release.
    /// </summary>
    /// <remarks>
    /// This test is the pin for the rejected alternative AT THIS LAYER: the fixture supplies the
    /// evidence rows itself, so it catches a change that operates on the rows the ENGINE receives. On
    /// such a change, one that maps a shortened read to <c>ReducedCoverage</c> before the engine reads
    /// it, the elevated row stops being <c>Available</c>, the gate stops firing, tier 5 relaxes the
    /// hold, and the second assertion below fails on the ACTION. That is the fail-open the ruling
    /// exists to avoid, and it is why the assertion is on the action rather than on the tier's name.
    /// <para>
    /// It does NOT catch the same mapping made in the PRODUCER, because the fixture never runs Nimble:
    /// a classifier that emitted <c>ReducedCoverage</c> for a shortened read would leave this test
    /// green while the shipped path fail-opened. That half is pinned on its own side by
    /// <c>NimbleSemanticMailClassifierTests.Reports_the_shortening_as_a_reason_when_the_fit_shortened_the_body</c>,
    /// at <c>tests/StyloMail.Nimble.Tests/NimbleSemanticMailClassifierTests.cs:470</c>, which asserts
    /// over a forced-shortening fixture that every answered row stays <c>Available</c> and carries the
    /// population control for it. Named because a green here is not a guard over there: the two
    /// assertions are one guard together and neither is one alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_shortened_row_still_elevates_the_security_gate()
    {
        // CONTROL FIRST: a marketing-shaped hold that recipient preference relaxes on its own. If this
        // does not allow, the preference path is not running in this fixture and the test below would
        // be asserting a hold for the wrong reason.
        var marketingOnly = new[] { Semantic("semantic.unsolicited_solicitation", 0.7) };
        var relaxed = Decide(marketingOnly, new PolicyOptions { DimensionWeights = Weights });
        Assert.Equal(MailAction.Allow, relaxed.Action);

        // SUBJECT: the same marketing hold beside a credential-request row that was answered over a
        // SHORTENED body. The row is Available, so the gate fires and the hold STANDS.
        //
        // THE VALUES ARE A BAND, NOT A TASTE. `Weights` gives these two dimensions EQUAL weight, so
        // the aggregate index is their MEAN, and the fixture has to sit in
        // [HoldThreshold, QuarantineThreshold) or the path under test is not the one asserted: at
        // 0.9 and 0.7 the mean is 0.8, which is AT the quarantine threshold, so the engine returns
        // MailAction.Quarantine from the risk path with no relaxable marking and tier 5 is never
        // reached. That was this test's first run and it is why the values moved by 0.05. The
        // `RelaxableByRecipientPreference` assertion below is the control against a future re-tune:
        // if a value moves the mean to or past the quarantine threshold, that assertion fails too,
        // so widening the action assertion cannot paper over it.
        var withShortenedSecurityRow = new[]
        {
            Semantic("semantic.unsolicited_solicitation", 0.7),
            Semantic("semantic.credential_request", 0.85, PromptShortenedReason),
        };
        var held = Decide(withShortenedSecurityRow, new PolicyOptions { DimensionWeights = Weights });
        Assert.Equal(MailAction.Hold, held.Action);

        // And the FLAG IS TRUE, which is the assertion I first wrote backwards and then removed.
        // The hold in this fixture is the marketing one, taken at `MailPolicyEngine.cs:415` as the
        // single site that marks itself relaxable, so `RelaxableByRecipientPreference` is TRUE and
        // the hold STANDS because the GATE blocks the relaxation rather than because the hold is
        // unmarked. Quoted whole, because a two-conjunct gloss would be wrong: `:198-200` at HEAD
        // 96a19fa is
        //     if (decision.RelaxableByRecipientPreference
        //         && input.Context.RecipientPrefersThisTrafficClass
        //         && !HasElevatedSecuritySignal(input.Evidence))
        // so tier 5 needs the hold's own marking AND the context's preference AND no elevated
        // security row. This fixture sets `RecipientPrefersThisTrafficClass` in its context for
        // exactly that reason, and the assertion below would be testing a path that never runs if it
        // did not. So the pair of assertions pins the mechanism at its own site: the flag says tier 5
        // MAY relax, the context says the recipient ASKED for it, and an elevated security row says
        // NOT THIS ONE. Asserting the flag false here would have been asserting the opposite of what
        // the code does.
        Assert.True(held.RelaxableByRecipientPreference);
    }
}
