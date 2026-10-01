using StyloMail.Core;

namespace StyloMail.Policy.Tests;

public sealed class MailPolicyEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal)
    {
        ["semantic.credential_request"] = 1.0,
        ["semantic.unsolicited_solicitation"] = 1.0,
    };

    /// <summary>
    /// The shipped continuity weight (0.5) beside one ordinary dimension (1.0), so an excluded row's
    /// effect on the index and on coverage is visible in the arithmetic rather than argued. Under
    /// decision 32 the excluded dimension leaves both totals, so the denominators here are 1.0, not
    /// the 1.5 the table would suggest and not the 1.5 decision 31 left behind.
    /// </summary>
    private static readonly Dictionary<string, double> ContinuityWeights = new(StringComparer.Ordinal)
    {
        [SemanticDimensions.ConversationalContinuityId] = 0.5,
        ["semantic.credential_request"] = 1.0,
    };

    [Fact]
    public void The_kill_switch_defers_rather_than_rejects()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { EmergencyKillSwitchEngaged = true });

        // An operational incident must not become the sender's permanent mail loss.
        Assert.Equal(MailAction.Defer, decision.Action);
        Assert.Equal("resource-controls", decision.DecidedBy);
    }

    [Fact]
    public void An_allowlist_entry_does_not_bypass_the_kill_switch()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { EmergencyKillSwitchEngaged = true, AllowlistEntryValid = true });

        Assert.Equal(MailAction.Defer, decision.Action);
    }

    [Fact]
    public void An_exhausted_quota_defers_outbound_submission()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { OutboundQuotaExhausted = true },
            MailDirection.Outbound);

        Assert.Equal(MailAction.Defer, decision.Action);
    }

    [Fact]
    public void A_verified_violation_rejects_without_consulting_the_model()
    {
        // Risk index is low and every dimension is masked, the verified rule still decides.
        var decision = Decide(
            Risk(0.0, 0.0),
            Context() with { VerifiedSecurityRuleViolations = ["dmarc.reject"] });

        Assert.Equal(MailAction.Reject, decision.Action);
        Assert.Equal("verified-rules", decision.DecidedBy);
        Assert.Contains("dmarc.reject", decision.Reasons[0].Message);
    }

    /// <summary>
    /// A checkable fact that must refuse, one case per approved member, read from the evidence rather
    /// than from anything the caller has to populate. The fact is established rather than inferred,
    /// like a violation, but it refutes a delivery rather than the message: each approved member has a
    /// legitimate-traffic population that a Reject would destroy and a review can release. The index
    /// is low and fully covered on purpose, to show the refusal is taken above the risk path rather
    /// than derived from the arithmetic.
    /// </summary>
    [Theory]
    [InlineData("deterministic.trusted_authentication_failure")]
    [InlineData("deterministic.link_idn_homograph")]
    [InlineData("deterministic.attachment_type_mismatch")]
    public void A_checkable_fact_that_must_refuse_holds_and_never_rejects(string finding)
    {
        var evidence = new[] { Deterministic(finding, 1.0) };

        var decision = Decide(Risk(0.0, 1.0), Context(), evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal("verified-rules", decision.DecidedBy);
        Assert.Equal("policy.refusing_finding", decision.Reasons[0].Code);
        Assert.Contains(finding, decision.Reasons[0].Message);

        // A Hold is bounded, exactly as a risk-driven one is.
        Assert.NotNull(decision.ReEvaluateBy);
    }

    [Fact]
    public void Only_a_present_finding_refuses_so_a_clean_zero_and_an_outage_are_not_refusals()
    {
        // The refusal reads the three states the wire already carries, and they have to read apart:
        // a measured mismatch refuses, a measured zero does not, and neither never-asked
        // (NotApplicable) nor asked-and-unanswered (Unavailable) is a fact about the message. The
        // last two are the cases that matter most: an outage is not a verdict about the mail, and a
        // rule that read one as a verdict would be a fail-closed rule pretending to be a detection.
        //
        // The index is held low and the coverage full on purpose, as in the theory above: what is
        // under test is which row states establish a refusal, not the arithmetic behind it.
        var calm = Deterministic(DeterministicFindings.ThreadHeaderConsistency, 0.0);

        foreach (var (availability, value) in new (EvidenceAvailability, double?)[]
        {
            (EvidenceAvailability.Available, 0.0),
            (EvidenceAvailability.NotApplicable, null),
            (EvidenceAvailability.Unavailable, null),
        })
        {
            var row = Deterministic(DeterministicFindings.TrustedAuthenticationFailure, 0.0)
                with { Availability = availability, Value = value };

            var decision = Decide(
                Risk(0.0, 1.0),
                Context(),
                evidence: [calm, row]);

            Assert.Equal(MailAction.Allow, decision.Action);
            Assert.DoesNotContain(decision.Reasons, r => r.Code == "policy.refusing_finding");
        }
    }

    [Fact]
    public void A_finding_that_is_index_only_stays_out_of_the_refusal()
    {
        // The other seven are weighted but never refuse: their false positives are structural rather
        // than rare, so a refusal rule would hold a large share of ordinary mail. This is the pin that
        // catches a later widening of the refusing set.
        var evidence = new[]
        {
            Deterministic(DeterministicFindings.LinkDisplayMismatch, 1.0),
            Deterministic(DeterministicFindings.DisplayNameAddressMismatch, 1.0),
            Deterministic(DeterministicFindings.PaddingObfuscation, 1.0),
        };

        var decision = Decide(Risk(0.0, 1.0), Context(), evidence: evidence);

        Assert.DoesNotContain(decision.Reasons, r => r.Code == "policy.refusing_finding");
    }

    [Fact]
    public void An_allow_says_when_a_refusal_check_was_not_evaluated_rather_than_reading_as_clean()
    {
        // The one thing carried over from the deleted caller-supplied field, re-keyed to the evidence:
        // a delivery must not read as checked-and-clean when the check never ran. Re-keying it also
        // narrows it, which is the point. Under the field every allow carried the note because nothing
        // populated it; here it appears only where a refusal check really was in question and
        // unattempted.
        Evidence RefusingRow(string id, EvidenceAvailability availability, double? value) =>
            Deterministic(id) with { Availability = availability, Value = value };

        var calm = Deterministic(DeterministicFindings.ThreadHeaderConsistency, 0.0);

        // Every refusing check was measured and answered, so nothing is unevaluated.
        var measured = DeterministicFindings.Refusing
            .Select(id => RefusingRow(id, EvidenceAvailability.Available, 0.0))
            .Append(calm)
            .ToArray();

        var clean = Decide(Risk(0.0, 1.0), Context(), evidence: measured);

        Assert.Equal(MailAction.Allow, clean.Action);
        Assert.DoesNotContain(
            clean.Reasons,
            r => r.Code == "policy.refusing_findings_not_evaluated");

        // Never in question is not the same as not evaluated. A message that raises none of the three
        // questions allows WITHOUT the note, because a deployment's shape is not an outage.
        var inapplicable = DeterministicFindings.Refusing
            .Select(id => RefusingRow(id, EvidenceAvailability.NotApplicable, null))
            .Append(calm)
            .ToArray();

        var neverAsked = Decide(Risk(0.0, 1.0), Context(), evidence: inapplicable);

        Assert.Equal(MailAction.Allow, neverAsked.Action);
        Assert.DoesNotContain(
            neverAsked.Reasons,
            r => r.Code == "policy.refusing_findings_not_evaluated");

        // Asked and unanswered, and absent entirely, both leave a check that did not run.
        var unanswered = DeterministicFindings.Refusing
            .Select(id => RefusingRow(id, EvidenceAvailability.Unavailable, null))
            .Append(calm)
            .ToArray();

        foreach (var evidence in new[] { unanswered, new[] { calm } })
        {
            var decision = Decide(Risk(0.0, 1.0), Context(), evidence: evidence);

            Assert.Equal(MailAction.Allow, decision.Action);

            var note = Assert.Single(
                decision.Reasons,
                r => r.Code == "policy.refusing_findings_not_evaluated");

            foreach (var id in DeterministicFindings.Refusing)
            {
                Assert.Contains(id, note.EvidenceSignalIds);
            }

            // Appended, never prepended: the reason that produced the decision is still read first.
            Assert.Equal("policy.risk_below_threshold", decision.Reasons[0].Code);
        }
    }

    [Fact]
    public void A_refusing_finding_is_not_relaxed_by_a_recipient_preference()
    {
        // Tier 5 can relax a preference-shaped hold, and it must never relax this one. That is the
        // reason the refusal is taken in tier 2 and returns before tier 5 runs: the fact is a refusal
        // about the delivery, not a preference disagreement.
        var evidence = new[] { Deterministic(DeterministicFindings.LinkIdnHomograph, 2.0) };

        var decision = Decide(
            Risk(0.0, 1.0),
            Context() with { RecipientPrefersThisTrafficClass = true },
            evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal("verified-rules", decision.DecidedBy);
    }

    [Fact]
    public void A_verified_violation_outranks_a_refusing_finding_because_a_reject_is_the_stronger_verdict()
    {
        // Both are tier-2 verdicts about established facts. A permanent verdict must not be softened
        // into a reviewable one by a coexisting refusal, so the violations are read first and this pin
        // catches any later reordering of the two.
        var evidence = new[] { Deterministic(DeterministicFindings.AttachmentTypeMismatch, 1.0) };

        var decision = Decide(
            Risk(0.0, 1.0),
            Context() with { VerifiedSecurityRuleViolations = ["dmarc.reject"] },
            evidence: evidence);

        Assert.Equal(MailAction.Reject, decision.Action);
        Assert.Equal("verified-rules", decision.DecidedBy);
        Assert.DoesNotContain("deterministic.attachment_type_mismatch", decision.Reasons[0].Message);
    }

    [Fact]
    public void An_unresolved_suspected_compromise_stays_quarantined()
    {
        var decision = Decide(
            Risk(0.05, 1.0),
            Context() with { BaselineFrozenForSuspectedCompromise = true },
            MailDirection.Outbound);

        // Low current risk does not decay the posture back to delivery.
        Assert.Equal(MailAction.Quarantine, decision.Action);
        Assert.Equal("compromise-posture", decision.DecidedBy);
    }

    [Fact]
    public void Inbound_traffic_is_not_quarantined_by_an_outbound_compromise_posture()
    {
        var evidence = new[] { Deterministic() };

        var decision = Decide(
            Risk(0.05, 1.0, evidence),
            Context() with { BaselineFrozenForSuspectedCompromise = true },
            MailDirection.Inbound,
            evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    [Fact]
    public void High_risk_on_thin_coverage_holds_rather_than_quarantines()
    {
        // Index is above the quarantine threshold, but only 40% of weight was covered.
        var decision = Decide(Risk(0.95, 0.40), Context());

        // An irreversible action on a fraction of the evidence is exactly what we refuse.
        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_for_irreversible");
    }

    [Fact]
    public void High_risk_on_full_coverage_quarantines()
    {
        var decision = Decide(Risk(0.95, 1.0), Context());

        Assert.Equal(MailAction.Quarantine, decision.Action);
    }

    [Fact]
    public void Low_risk_on_full_coverage_is_allowed()
    {
        var evidence = new[] { Deterministic() };

        var decision = Decide(Risk(0.05, 1.0, evidence), Context(), evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    /// <summary>
    /// The flip this gate exists for. A provider that answers every dimension with a confident
    /// false negative raises the covered fraction and lowers the index, so both coverage guards
    /// pass and the message looks calm rather than unmeasured. Nothing in the evidence can be
    /// checked against the message itself, so the tier holds instead of delivering on the model's
    /// word. The reason names the provider that made the claim.
    /// </summary>
    [Fact]
    public void A_low_index_carried_only_by_a_model_holds_rather_than_allows()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Signal("semantic.unsolicited_solicitation", 0.0),
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        // The guards cannot see it: full coverage, index 0.0, indistinguishable from a clean
        // message that was actually measured.
        Assert.Equal(1.0, risk.CoveredWeightFraction);
        Assert.Equal(0.0, risk.Index);

        var decision = Decide(risk, Context(), evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal("policy.allow_uncorroborated_by_deterministic_evidence", decision.Reasons[0].Code);
        Assert.Contains("jev-1.13.0", decision.Reasons[0].Message);
    }

    /// <summary>
    /// The gate's explanation, rendered for both branches, because the code alone was pinned and the
    /// prose was not: the branch that fires whenever semantic evidence is present read "and a
    /// probabilistic negative may authorise delivery on its own", the exact inverse of the rule that
    /// had just held. A code-only assertion let that through, so the message is what is asserted
    /// here. Filed medium by `nimble-`; the decision never changed, only what the ledger said about
    /// it.
    /// </summary>
    [Fact]
    public void The_uncorroborated_allow_reason_states_the_prohibition_in_both_branches()
    {
        // Branch A: the model answered, so there is evidence to name, and the branch produced the
        // inverted sentence. This is the branch the gate exists for.
        var modelOnly = new[] { Signal("semantic.credential_request", 0.0) };

        var withProvider = Decide(
            CompositeRiskScorer.Compute(modelOnly, Weights),
            Context(),
            evidence: modelOnly);

        Assert.Equal(MailAction.Hold, withProvider.Action);
        Assert.Equal("policy.allow_uncorroborated_by_deterministic_evidence", withProvider.Reasons[0].Code);
        Assert.Contains("jev-1.13.0", withProvider.Reasons[0].Message);
        Assert.Contains("no probabilistic negative may authorise", withProvider.Reasons[0].Message);
        Assert.DoesNotContain("and a probabilistic negative", withProvider.Reasons[0].Message);

        // Branch B: nothing was measured at all. Only reachable once the deployment states it
        // expects no semantic coverage, which is what puts the index under the gate rather than
        // under the coverage floor.
        var options = Options();
        options.MinimumCoverageForAllow = 0.0;

        var nothingMeasured = Decide(Risk(0.0, 0.0), Context(), options: options);

        Assert.Equal(MailAction.Hold, nothingMeasured.Action);
        Assert.Equal("policy.allow_uncorroborated_by_deterministic_evidence", nothingMeasured.Reasons[0].Code);
        Assert.Contains("No evidence was available at all, and no probabilistic negative", nothingMeasured.Reasons[0].Message);
    }

    [Fact]
    public void A_low_index_corroborated_by_deterministic_evidence_still_allows()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Signal("semantic.unsolicited_solicitation", 0.0),
            Deterministic(),
        };

        var decision = Decide(
            CompositeRiskScorer.Compute(evidence, Weights),
            Context(),
            evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    /// <summary>
    /// Behavioural evidence is admissible and is not corroboration. It is learned, and the
    /// attacker shapes the traffic that produces it, so it must not be the thing that carries an
    /// allow when the semantic provider has gone quiet.
    /// </summary>
    [Fact]
    public void Behavioural_evidence_alone_does_not_corroborate_an_allow()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Signal("behavioural.sender_first_contact", 0.2, EvidenceOrigin.Behavioural),
        };

        var decision = Decide(
            CompositeRiskScorer.Compute(evidence, Weights),
            Context(),
            evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
    }

    /// <summary>
    /// Deterministic origin is a producer's stamp, not a claim that the row came from the message.
    /// The two are close enough in practice to be confused, and the row that proves they differ is
    /// <c>assessment.behavioural_context</c>: deterministic origin, source is the sender's profile
    /// store, and <c>Unavailable</c>. So this pins the property the gate actually reads, the
    /// availability, on a row whose origin label alone would have admitted it.
    /// </summary>
    /// <remarks>
    /// <b>Why this is a Hold and not an Allow.</b> Nothing about this message was measured by the
    /// pipeline, so a calm index has nothing checkable standing behind it, which is the exact shape
    /// the gate exists to refuse. A gate that keyed on the origin label would find the row and
    /// allow, so the availability is not a detail of the implementation, it is the property.
    /// <para>
    /// The label is still sound today only because of facts that live outside Policy: every
    /// <c>Available</c> row with deterministic origin that is not derived from the bytes
    /// (<c>assessment.hard_limit_violation</c>) is emitted only on a path that also populates
    /// <c>VerifiedSecurityRuleViolations</c>, so policy rejects before it reaches this gate. That is
    /// a coincidence of the current emission set, not a guarantee this gate holds, and it is
    /// recorded here so a later reader does not take the label for the property.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_deterministic_row_that_is_unavailable_does_not_corroborate()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Signal("semantic.unsolicited_solicitation", 0.0),
            new Evidence
            {
                SignalId = "assessment.behavioural_context",
                Origin = EvidenceOrigin.Deterministic,
                Availability = EvidenceAvailability.Unavailable,
                Value = null,
                Confidence = null,
                SourceVersion = "assessment-1.0.0",
                ObservedAt = Now,
            },
        };

        var decision = Decide(Risk(0.0, 1.0, evidence), Context(), evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal(
            "policy.allow_uncorroborated_by_deterministic_evidence",
            decision.Reasons[0].Code);
    }

    /// <summary>
    /// The flip the filed gap predicted: <c>deterministic.link_display_mismatch</c> now carries a
    /// weight with a declared unit, so it is counted, and the questions this message did not pose
    /// have left the denominator. The fixture supplies two semantic rows and one deterministic row
    /// against a shipped table whose denominator is the semantic backbone plus whatever deterministic
    /// questions this message poses, so its counted weight buys less coverage than it did when the
    /// rows outside <c>DimensionWeights</c> were ignored entirely.
    /// </summary>
    /// <remarks>
    /// Was <c>A_concerning_unweighted_deterministic_finding_does_not_yet_block_an_allow</c>; renamed
    /// because the answer changed. Decision 42, issue `unweighted-deterministic-findings-cannot-block-a`.
    /// </remarks>
    [Fact]
    public void A_concerning_deterministic_finding_now_enters_the_denominator_and_holds()
    {
        var options = new PolicyOptions();
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Deterministic(DeterministicFindings.LinkDisplayMismatch, 1.0),
            Deterministic(),
        };

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        // Counted, where before it was invisible to the arithmetic.
        Assert.Contains(DeterministicFindings.LinkDisplayMismatch, risk.ContributingSignalIds);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        // Held on the coverage floor, and named: on this evidence the finding is counted and the
        // index is 0.5, under the hold threshold. What moves the message is the weight that entered
        // the denominator, not the objection crossing a threshold. The next test measures what
        // happens when the whole question set is answered instead.
        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    /// <summary>
    /// The measured limit of the repair, pinned rather than argued, and filed as a finding. On a
    /// message whose whole question set was answered, one deterministic objection at the shipped
    /// weight of 1.0 moves the index to <c>1.0 / 8.3 = 0.120</c>, which is under every threshold, so
    /// the message is still allowed.
    /// </summary>
    /// <remarks>
    /// This is the arithmetic of a weighted mean and not a defect in the wiring: with the semantic
    /// backbone in the denominator at 7.3, a single deterministic weight of <c>w</c> can move the
    /// index by at most <c>w / (7.3 + w)</c>, which is 0.120 at <c>w = 1.0</c> and would need
    /// <c>w >= 8.9</c> to reach the 0.55 hold threshold on its own. So the repair makes a checkable
    /// fact <em>count</em>; it does not make one checkable objection <em>block</em>. Raising the
    /// weights to that size would dilute every semantic index by more than half, so the two are the
    /// same knob and neither ordering is free. Reported to `overview-` as a measurement, not
    /// resolved here.
    /// </remarks>
    [Fact]
    public void A_single_deterministic_objection_moves_the_index_but_does_not_block_a_fully_measured_message()
    {
        var options = new PolicyOptions();
        var evidence = SemanticDimensions.All
            .Where(dimension => dimension.Id != SemanticDimensions.ConversationalContinuityId)
            .Select(dimension => Signal(dimension.Id, 0.0))
            .Append(Deterministic(DeterministicFindings.LinkDisplayMismatch, 1.0))
            .ToArray();

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(1.0 / 8.3, risk.Index, precision: 9);
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 12);
        Assert.True(risk.Index < options.HoldThreshold);

        Assert.Equal(
            MailAction.Allow,
            Decide(risk, Context(), options: options, evidence: evidence).Action);
    }

    /// <summary>
    /// The counterexample that kept the corroboration gate beside the coverage floor rather than
    /// folded into it (decision 42). A plain-text message poses no deterministic question, so those
    /// rows leave the denominator and coverage is high on the semantic answers alone. Coverage
    /// crosses the allow floor; the presence check still refuses, because a floor answers "did we
    /// answer the questions we could ask" and cannot answer "did a non-probabilistic check agree".
    /// Stated in that order on purpose: first the crossing, then the refusal.
    /// </summary>
    /// <summary>
    /// The provider-down reading, measured rather than inferred: the one place decision 42 moved a
    /// message in the permissive direction, and the closure of it. The semantic rows are unanswered
    /// and stay in the denominator; the deterministic questions are all asked and all answered calm,
    /// and they now carry weight, so coverage is <c>7.7 / 15.0 = 0.513</c> and clears the allow floor
    /// on structure alone. The tier refuses it.
    /// </summary>
    /// <remarks>
    /// Before decision 42 this message held at coverage 0.0, because the deterministic rows carried
    /// no weight and nothing else was covered. It then allowed at 0.513, which was reported to
    /// <c>overview-</c> as a measurement and ruled a fail-open: a semantic blackout must not be
    /// convertible into a delivery by a clean structural read, because the structural layer is what
    /// an adversary satisfies by construction. The arithmetic is asserted unchanged here and the
    /// action is the refusal, so the two halves stay readable apart. The second half is the same
    /// outage on a message that poses no deterministic question: still held, on coverage, unchanged.
    /// </remarks>
    [Fact]
    public void A_provider_down_message_cannot_allow_on_its_deterministic_questions_alone()
    {
        var options = new PolicyOptions();

        var evidence = SemanticBackbone(EvidenceAvailability.Unavailable)
            .Concat(DeterministicFindings.Units.Keys.Select(id => Deterministic(id, 0.0)))
            .ToArray();

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(7.7, risk.CoveredWeight, precision: 9);
        Assert.Equal(15.0, risk.CoveredWeight / risk.CoveredWeightFraction, precision: 9);
        Assert.True(risk.CoveredWeightFraction >= options.MinimumCoverageForAllow);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal("policy.allow_without_a_semantic_answer", decision.Reasons[0].Code);
        Assert.DoesNotContain(decision.Reasons, r => r.Code == "policy.risk_below_threshold");

        // The same outage on a message that poses no deterministic question still holds: its rows
        // leave the denominator, so there is nothing to cover and the floor refuses.
        var plainText = SemanticBackbone(EvidenceAvailability.Unavailable);

        var plainRisk = CompositeRiskScorer.Compute(plainText, options.DimensionWeights);

        Assert.Equal(0.0, plainRisk.CoveredWeightFraction);
        Assert.Equal(
            MailAction.Hold,
            Decide(plainRisk, Context(), options: options, evidence: plainText).Action);
    }

    /// <summary>
    /// The two configurations the refusal has to tell apart, and the difference between them is
    /// whether the semantic questions were asked at all. A provider that was never asked reports
    /// <c>NotApplicable</c> and leaves no silence to read, so a low index over the deterministic
    /// answers allows; a semantic row that was asked and left unanswered refuses, whatever the allow
    /// floor is.
    /// </summary>
    /// <remarks>
    /// Askability follows configuration and not runtime availability. The floor-at-zero case is the
    /// sharper of the two: the gate is absolute rather than exempted by the floor, because the floor
    /// at zero is the one configuration where a full blackout would otherwise allow on local evidence
    /// alone. A deployment with no semantic provider at all never reaches policy (the unconfigured
    /// host's assessor throws before an assessment exists), so the only deployment that can decide
    /// without semantic answers is one whose configured provider is in a runtime outage. That a
    /// deployment's shape is only distinguishable by inspection of the evidence is the gap the
    /// configuration work closes.
    /// </remarks>
    [Fact]
    public void The_semantic_refusal_reads_a_question_that_was_asked_and_not_a_deployment_that_never_asks()
    {
        var options = new PolicyOptions();
        var deterministic = DeterministicFindings.Units.Keys
            .Select(id => Deterministic(id, 0.0))
            .ToArray();

        var neverAsked = SemanticBackbone(EvidenceAvailability.NotApplicable)
            .Concat(deterministic)
            .ToArray();

        Assert.Equal(
            MailAction.Allow,
            Decide(
                CompositeRiskScorer.Compute(neverAsked, options.DimensionWeights),
                Context(),
                options: options,
                evidence: neverAsked).Action);

        // The absolute gate: the exemption by allow floor is deleted, so an unanswered semantic
        // question refuses even in the configuration that declares itself local-evidence-only.
        var asked = SemanticBackbone(EvidenceAvailability.Unavailable)
            .Concat(deterministic)
            .ToArray();

        var declared = new PolicyOptions { MinimumCoverageForAllow = 0.0 };

        var refused = Decide(
            CompositeRiskScorer.Compute(asked, declared.DimensionWeights),
            Context(),
            options: declared,
            evidence: asked);

        Assert.Equal(MailAction.Hold, refused.Action);
        Assert.Equal("policy.allow_without_a_semantic_answer", refused.Reasons[0].Code);
    }

    [Fact]
    public void A_feature_poor_message_cannot_allow_on_semantic_answers_alone()
    {
        var options = new PolicyOptions();
        var evidence = SemanticDimensions.All
            .Where(dimension => dimension.Id != SemanticDimensions.ConversationalContinuityId)
            .Select(dimension => Signal(dimension.Id, 0.0))
            .ToArray();

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        // The crossing: coverage is at its floor, not below it.
        Assert.True(risk.CoveredWeightFraction >= options.MinimumCoverageForAllow);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        // The refusal: not one deterministic row was measured.
        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Equal("policy.allow_uncorroborated_by_deterministic_evidence", decision.Reasons[0].Code);
        Assert.DoesNotContain(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    /// <summary>
    /// A total semantic outage yields index 0.0 over coverage 0.0, under every threshold. Before
    /// this was fixed, the engine returned Allow, making absence of evidence indistinguishable from
    /// evidence of safety. Reported by `assess-`, which had to guard it in wiring.
    /// </summary>
    [Fact]
    public void A_total_outage_does_not_produce_an_allow()
    {
        var decision = Decide(Risk(0.0, 0.0), Context());

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    [Fact]
    public void An_allow_requires_enough_coverage_to_be_meaningful()
    {
        var evidence = new[] { Deterministic() };

        // Just under the floor: not enough evidence to conclude the message is safe.
        Assert.Equal(MailAction.Hold, Decide(Risk(0.0, 0.29), Context()).Action);

        // At the floor: judgement is meaningful again.
        Assert.Equal(
            MailAction.Allow,
            Decide(Risk(0.0, 0.30, evidence), Context(), evidence: evidence).Action);
    }

    /// <summary>
    /// The allow-floor crossing decision 32 creates, pinned on a <b>real</b> weight set rather than
    /// forced with a synthetic one. The counted weight here is 2.2, which is reachable as
    /// <c>credential_request</c> 1.0 + <c>link_lure</c> 0.8 + <c>urgency_pressure</c> 0.4, and it is
    /// the lowest of the two real sums inside the window: a counted weight <c>c</c> in
    /// <c>[2.19, 2.34)</c> sat below <c>MinimumCoverageForAllow</c> against decision 31's 7.8
    /// denominator and sits at or above it against decision 32's 7.3. Enumerating the subset sums of
    /// the eleven remaining weights gives 2.2 and 2.3 in that window, and no other.
    /// </summary>
    /// <remarks>
    /// Before the exclusion this evidence produced <c>policy.insufficient_coverage_to_allow</c> and a
    /// bounded Hold, coverage 2.2/7.8 = 0.282051 being under the 0.30 floor. It produces an Allow now,
    /// at 2.2/7.3 = 0.301370. So the row leaving the denominator is not inert at shipped thresholds:
    /// on this counted weight the message moves from held to delivered. That is the intended
    /// behaviour, because a dimension excluded by policy is not an unmeasured one and must not sit in
    /// the coverage denominator as a permanent shortfall, but it is a decision change and it is pinned
    /// rather than described.
    /// <para>
    /// The corpus does not exercise it, and the pin should not imply that it did: `ingress-` measured
    /// all eleven counted dimensions Available on all eight e2e arms (1 Oct), so covered equals askable
    /// and coverage is 1.0 against the 7.3 denominator, with nothing partially answering. The window
    /// is reachable in the arithmetic and unexercised by those arms by construction.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_counted_weight_of_2_2_crosses_the_allow_floor_because_the_excluded_row_left_the_denominator()
    {
        var options = new PolicyOptions();
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.1),
            Signal("semantic.link_lure", 0.1),
            Signal("semantic.urgency_pressure", 0.1),
            Deterministic(),
        };

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(2.2, risk.CoveredWeight, precision: 9);

        // Re-derived at decision 42: this message poses no deterministic question (none of the ten
        // appears), so the denominator is the semantic backbone, 7.3, which is the smallest
        // achievable denominator. The window is therefore unmoved at the floor: the lowest exact
        // tenth above 0.30 * 7.3 = 2.19 is still 2.2, and 2.1 is still under. A negative is a
        // reading: membership is unchanged here, and it changes only for a message that poses more.
        Assert.Equal(7.3, risk.CoveredWeight / risk.CoveredWeightFraction, precision: 9);

        // The crossing, stated as the two fractions rather than as a bare number: the superseded
        // denominator is the literal 7.8, which is what decision 31 divided by, and the two sit on
        // opposite sides of the floor.
        Assert.True(2.2 / 7.8 < options.MinimumCoverageForAllow);
        Assert.Equal(2.2 / 7.3, risk.CoveredWeightFraction, precision: 9);
        Assert.True(risk.CoveredWeightFraction >= options.MinimumCoverageForAllow);

        Assert.Equal(MailAction.Allow, Decide(risk, Context(), options: options, evidence: evidence).Action);
    }

    /// <summary>
    /// The same crossing on the other shipped floor, measured at the same time because it is the same
    /// class of consequence and nobody had looked: a counted weight of 4.4 (the five heaviest
    /// dimensions) sat below <c>MinimumCoverageForIrreversibleAction</c> at 4.4/7.8 = 0.564103 and
    /// sits at or above it at 4.4/7.3 = 0.602740. Enumerating the subset sums gives 4.4, 4.5 and 4.6
    /// in that window. So a high-index message that decision 31 held for review on thin coverage is
    /// quarantined now, which is a change in the opposite direction to the allow crossing and the one
    /// an operator would notice.
    /// <para>
    /// Not exercised by the eight e2e arms: every counted dimension is Available on all eight
    /// (`ingress-`, 1 Oct), so nothing sits at a partially covered weight.
    /// </para>
    /// </summary>
    [Fact]
    public void A_counted_weight_of_4_4_crosses_the_irreversible_floor_because_the_excluded_row_left_the_denominator()
    {
        var options = new PolicyOptions();
        var evidence = new[]
        {
            Signal("semantic.credential_request", 1.0),
            Signal("semantic.payment_redirection", 1.0),
            Signal("semantic.link_lure", 1.0),
            Signal("semantic.attachment_lure", 1.0),
            Signal("semantic.sensitive_data_request", 1.0),
        };

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(4.4, risk.CoveredWeight, precision: 9);
        Assert.True(risk.Index >= options.QuarantineThreshold);

        // Re-derived at decision 42, at the same minimum denominator as the allow window: three
        // exact tenths above 0.60 * 7.3 = 4.38, so 4.4 is still the smallest, and membership is
        // unchanged.
        Assert.Equal(7.3, risk.CoveredWeight / risk.CoveredWeightFraction, precision: 9);

        Assert.True(4.4 / 7.8 < options.MinimumCoverageForIrreversibleAction);
        Assert.Equal(4.4 / 7.3, risk.CoveredWeightFraction, precision: 9);
        Assert.True(risk.CoveredWeightFraction >= options.MinimumCoverageForIrreversibleAction);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        Assert.Equal(MailAction.Quarantine, decision.Action);
        Assert.DoesNotContain(decision.Reasons, r => r.Code == "policy.insufficient_coverage_for_irreversible");
    }

    /// <summary>
    /// Just below the lowest crossing, so the boundary has two sides. A counted weight of 2.1
    /// (<c>credential_request</c> 1.0 + <c>link_lure</c> 0.8 + <c>unsolicited_solicitation</c> 0.3)
    /// is under the floor in both denominators, 2.1/7.3 = 0.287671 being still below 0.30, so the
    /// exclusion did not move this message and the coverage guard still holds it. Without this the
    /// crossing test would pass just as well if the guard had been removed.
    /// </summary>
    [Fact]
    public void A_counted_weight_just_below_the_window_is_still_held_on_thin_coverage()
    {
        var options = new PolicyOptions();
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.1),
            Signal("semantic.link_lure", 0.1),
            Signal("semantic.unsolicited_solicitation", 0.1),
            Deterministic(),
        };

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(2.1, risk.CoveredWeight, precision: 9);
        Assert.True(risk.CoveredWeightFraction < options.MinimumCoverageForAllow);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        // The corroboration gate is satisfied and the index is calm; coverage alone is why this is
        // held, which is exactly the guard the crossing above rides through.
        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    /// <summary>
    /// The window as a formula, at both bounds of the denominator, in exact tenths computed in
    /// integers so that no float decides a boundary. A counted weight <c>c</c> flips a floor of
    /// <c>f</c> when <c>f(T - 0.5) &lt;= c &lt; fT</c>, where <c>T</c> is the askable total with the
    /// continuity row askable and not counted and <c>T - 0.5</c> is the denominator decision 32
    /// divides by. The width is <c>0.5f</c> whatever <c>T</c> is, so the window keeps its size and
    /// moves with the denominator: the ten deterministic questions add 7.7 of askable weight, which
    /// moves <c>T</c> from 7.8 to 15.5 and carries the window with it.
    /// </summary>
    /// <remarks>
    /// The range the single-instance pins below cover one point each of. Decision 42 adds the upper
    /// bound and leaves the lower one exactly where decision 32 put it, which is why the 2.2 and 4.4
    /// pins still hold and why each of them is the lower bound rather than the whole window.
    /// </remarks>
    [Fact]
    public void The_floor_windows_move_with_the_denominator_and_keep_their_width()
    {
        var options = new PolicyOptions();
        var allow = (int)Math.Round(options.MinimumCoverageForAllow * 100);
        var irreversible = (int)Math.Round(options.MinimumCoverageForIrreversibleAction * 100);

        // The lower bound is the semantic backbone (7.3 of denominator, 7.8 askable) and the upper is
        // the whole question set (15.0 and 15.5).
        const int backboneAskable = 78;
        const int fullAskable = 155;

        // In tenths throughout, so a counted weight is an integer C = 10c and the condition
        // f(T - 0.5) <= c < fT becomes, after multiplying by 1000 with f = factor / 100:
        // factor * (T10 - 5) <= 100 * C < factor * T10. No float takes part in the comparison.
        static List<double> TenthsInWindow(int factor, int askableTenths)
        {
            var lo = factor * (askableTenths - 5);
            var hi = factor * askableTenths;
            var tenths = new List<double>();

            for (var counted = 0; counted <= askableTenths; counted++)
            {
                var scaled = 100 * counted;
                if (scaled >= lo && scaled < hi)
                {
                    tenths.Add(counted / 10.0);
                }
            }

            return tenths;
        }

        Assert.Equal([2.2, 2.3], TenthsInWindow(allow, backboneAskable));
        Assert.Equal([4.5, 4.6], TenthsInWindow(allow, fullAskable));

        Assert.Equal([4.4, 4.5, 4.6], TenthsInWindow(irreversible, backboneAskable));
        Assert.Equal([9.0, 9.1, 9.2], TenthsInWindow(irreversible, fullAskable));

        // The width is the continuity row's own weight scaled by the floor, so it is identical at both
        // bounds: the window slides with the denominator and never stretches as a message poses more
        // questions.
        Assert.Equal(0.15, (allow * 5) / 1000.0, precision: 12);
        Assert.Equal(0.30, (irreversible * 5) / 1000.0, precision: 12);
    }

    /// <summary>
    /// The second window pin: the same counted weight, at the denominator a message with links now
    /// has. The 2.2 that clears the floor on a message whose deterministic questions were all
    /// inapplicable is under it once the link question is posed and left unanswered, because an
    /// unanswered question is in the denominator and not in the numerator.
    /// </summary>
    /// <remarks>
    /// This is the denominator consequence of decision 42 stated where it acts, and it is the honest
    /// counterweight to the crossing above: the floor is a fraction of the questions this message
    /// poses, so a feature-rich message needs more counted weight to clear it than a feature-poor
    /// one. Shifted the other way from the allow crossing and the one a tuning change would move.
    /// </remarks>
    [Fact]
    public void The_same_counted_weight_is_held_at_the_feature_rich_denominator()
    {
        var options = new PolicyOptions();
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.1),
            Signal("semantic.link_lure", 0.1),
            Signal("semantic.urgency_pressure", 0.1),
            new Evidence
            {
                SignalId = DeterministicFindings.LinkDisplayMismatch,
                Origin = EvidenceOrigin.Deterministic,
                Availability = EvidenceAvailability.Unavailable,
                Value = null,
                SourceVersion = "mime-1.0.0",
                ObservedAt = Now,
            },
        };

        var risk = CompositeRiskScorer.Compute(evidence, options.DimensionWeights);

        Assert.Equal(2.2, risk.CoveredWeight, precision: 9);

        // 8.3, not 7.3: the link question was posed and not answered, and it is in the denominator.
        Assert.Equal(8.3, risk.CoveredWeight / risk.CoveredWeightFraction, precision: 9);
        Assert.True(risk.CoveredWeightFraction < options.MinimumCoverageForAllow);

        var decision = Decide(risk, Context(), options: options, evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    /// <summary>
    /// The local-evidence-only deployment genuinely runs with no semantic coverage by design.
    /// Setting the floor to zero states that expectation explicitly rather than tolerating an
    /// outage by accident. Such a deployment is made of deterministic signals, which is what
    /// satisfies the corroboration requirement, so it needs nothing extra to allow.
    /// </summary>
    [Fact]
    public void A_local_evidence_only_deployment_can_opt_out_of_the_coverage_floor()
    {
        var options = Options();
        options.MinimumCoverageForAllow = 0.0;

        var evidence = new[] { Deterministic() };

        var decision = Decide(Risk(0.0, 0.0, evidence), Context(), options: options, evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    [Fact]
    public void A_hold_carries_a_deadline_within_the_absolute_ceiling()
    {
        var options = Options();
        options.HoldWindow = TimeSpan.FromMinutes(10);
        options.MaxHoldDeadline = TimeSpan.FromSeconds(30);

        var decision = Decide(Risk(0.60, 1.0), Context(), options: options);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.NotNull(decision.ReEvaluateBy);

        // A hold never extends silently beyond the ceiling, however the window is configured.
        Assert.True(decision.ReEvaluateBy <= Now.AddSeconds(30));
    }

    [Fact]
    public void An_allowlist_entry_relaxes_a_risk_hold()
    {
        var decision = Decide(
            Risk(0.60, 1.0),
            Context() with { AllowlistEntryValid = true });

        Assert.Equal(MailAction.Allow, decision.Action);
        Assert.Equal("allowlist", decision.DecidedBy);
    }

    [Fact]
    public void An_allowlist_entry_does_not_relax_a_quarantine()
    {
        var decision = Decide(
            Risk(0.95, 1.0),
            Context() with { AllowlistEntryValid = true });

        // Allowlists are scoped and expiring; they reach only as far as a hold.
        Assert.Equal(MailAction.Quarantine, decision.Action);
    }

    [Fact]
    public void Recipient_preference_relaxes_a_marketing_hold()
    {
        var evidence = new[] { Signal("semantic.unsolicited_solicitation", 0.7) };
        var decision = Decide(
            Risk(0.70, 1.0, evidence),
            Context() with { RecipientPrefersThisTrafficClass = true },
            evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
        Assert.Equal("recipient-preference", decision.DecidedBy);
    }

    [Fact]
    public void Recipient_preference_cannot_relax_a_security_hold()
    {
        // "Wanted promotions" is a legitimate preference; a payment-redirection change is not.
        var evidence = new[] { Signal("semantic.payment_redirection", 0.9) };
        var decision = Decide(
            Risk(0.70, 1.0, evidence),
            Context() with { RecipientPrefersThisTrafficClass = true },
            evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
    }

    [Fact]
    public void Masked_dimensions_are_recorded_rather_than_treated_as_zero()
    {
        var evidence = new[]
        {
            new Evidence
            {
                SignalId = "semantic.credential_request",
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Unavailable,
                Value = null,
                SourceVersion = "jev-1.13.0",
                ObservedAt = Now,
            },
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        // Both configured dimensions are masked: one explicitly Unavailable, the other absent
        // entirely. Absence and unavailability must be indistinguishable from each other and from
        // "not measured", and distinguishable from a measured zero.
        Assert.Equal(2, risk.Masked.Count);
        Assert.Contains(risk.Masked, m =>
            m.SignalId == "semantic.credential_request"
            && m.Availability == EvidenceAvailability.Unavailable);
        Assert.Contains(risk.Masked, m => m.SignalId == "semantic.unsolicited_solicitation");
        Assert.Equal(0.0, risk.CoveredWeightFraction);

        // If unavailable had been coerced to 0.0, the covered fraction would have been 1.0 and
        // the scorer would have reported a confident, calm result from no evidence at all.
        Assert.NotEqual(1.0, risk.CoveredWeightFraction);
    }

    /// <summary>
    /// Decision 32's wording, split by <c>overview-</c>'s ruling. The old sentence, "contributed
    /// nothing to the index and were masked, not treated as zero", is true of a row nothing was
    /// measured for and false of a row that carried an answer and was excluded by policy anyway. The
    /// top line is what a reader takes away, so the two cases are said plainly rather than left for a
    /// per-row reason string to disambiguate.
    /// </summary>
    [Fact]
    public void The_masked_dimensions_reason_says_the_two_cases_plainly()
    {
        var excluded = new MaskedDimension
        {
            SignalId = SemanticDimensions.ConversationalContinuityId,
            Availability = EvidenceAvailability.Available,
            Reason = "policy.dimension_excluded_pending_respecification: excluded from the index in both directions.",
        };

        var unmeasured = new MaskedDimension
        {
            SignalId = "semantic.credential_request",
            Availability = EvidenceAvailability.Unavailable,
        };

        // Both at once: a reader must be told about each kind, not shown one sentence that fits one.
        var both = MaskedReason([excluded, unmeasured]);
        Assert.Contains("were not measured", both, StringComparison.Ordinal);
        Assert.Contains("excluded by policy", both, StringComparison.Ordinal);

        // A row that was never measured must not be described as excluded by policy, and a policy
        // exclusion must not be described as a missing measurement. Each statement is absent when the
        // case it describes is absent.
        var onlyUnmeasured = MaskedReason([unmeasured]);
        Assert.Contains("were not measured", onlyUnmeasured, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded by policy", onlyUnmeasured, StringComparison.Ordinal);

        var onlyExcluded = MaskedReason([excluded]);
        Assert.Contains("excluded by policy", onlyExcluded, StringComparison.Ordinal);
        Assert.DoesNotContain("were not measured", onlyExcluded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decision 32, measured against the scorer rather than argued. Continuity is excluded in both
    /// directions, so its weight leaves the index's denominator as well as the numerator: the mean
    /// here is over 1.0 of weight, not the 1.5 the table declares, and not the 1.5 that decision 31
    /// left behind when it took only the numerator. That second denominator is the trap; a
    /// numerator-only exclusion leaves a permanent 0.5/1.5 coverage shortfall, and a constant
    /// shortfall is not a caveat, it is a number that looks like one and says nothing.
    /// </summary>
    [Fact]
    public void A_non_confirming_continuity_leaves_both_the_numerator_and_the_denominator()
    {
        var evidence = new[]
        {
            Signal(SemanticDimensions.ConversationalContinuityId, 0.0),
            Signal("semantic.credential_request", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        // Counted as a 0.0 it would have diluted 0.4 to 0.2: a calm index and raised coverage on a
        // message that was measured, which is the hazard shape decisions 23, 31 and 32 share.
        Assert.Equal(0.4 / 1.0, risk.Index, precision: 5);

        // Full coverage, not the 1.0/1.5 decision 31 recorded and not the 1.0/1.0-of-1.5 that only
        // removing the numerator would give. The excluded row is out of both totals.
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeight, precision: 5);

        // And the exclusion is visible rather than silent: an Available row that was not counted,
        // carrying the policy reason that says this is a ruling rather than a failed measurement.
        Assert.Contains(risk.Masked, m =>
            m.SignalId == SemanticDimensions.ConversationalContinuityId
            && m.Availability == EvidenceAvailability.Available
            && m.Reason is not null
            && m.Reason.StartsWith("policy.dimension_excluded_pending_respecification", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_confirming_continuity_cannot_lower_a_message_below_the_hold_threshold()
    {
        var evidence = new[]
        {
            Signal(SemanticDimensions.ConversationalContinuityId, 0.0),
            Signal("semantic.credential_request", 0.6),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        Assert.Equal(0.6, risk.Index, precision: 5);
        Assert.Equal(MailAction.Hold, Decide(risk, Context(), evidence: evidence).Action);
    }

    /// <summary>
    /// Decision 32 supersedes decision 31 here, and this is the case that shows why the exclusion is
    /// both directions rather than one-sided. Under 31 a confirming row counted, so a message that
    /// visibly belonged to its thread gained 0.5 of weight for that alone: 0.9/1.5. Decision 32
    /// measured the axis and found it is restatement rather than risk, so an A is the least
    /// informative message in the thread and an A here scores exactly what a B does.
    /// </summary>
    [Fact]
    public void A_confirming_continuity_is_excluded_too_and_adds_nothing()
    {
        var evidence = new[]
        {
            Signal(SemanticDimensions.ConversationalContinuityId, 1.0),
            Signal("semantic.credential_request", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        // 0.4/1.0, not the 0.9/1.5 the confirming row scored under decision 31: the confirmation no
        // longer counts, and it no longer carries its weight into the denominator either.
        Assert.Equal(0.4 / 1.0, risk.Index, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 5);

        // The row is masked on the confirming direction as well. "Excluded in both directions" has
        // to mean this case, or the rule is the old one-sided rule with a new reason string.
        Assert.Contains(risk.Masked, m =>
            m.SignalId == SemanticDimensions.ConversationalContinuityId
            && m.Availability == EvidenceAvailability.Available
            && m.Reason is not null);
    }

    /// <summary>
    /// The port's mid-point, now outside the question rather than inside it. Decision 31 had to name
    /// a threshold for "confirms" (a Noul above 0.5) because it kept one direction; decision 32
    /// excludes every value, so the midpoint is excluded for the same reason an A and a B are, and
    /// no boundary remains to be argued about.
    /// </summary>
    [Fact]
    public void A_balanced_continuity_is_excluded_like_every_other_value()
    {
        var evidence = new[]
        {
            Signal(SemanticDimensions.ConversationalContinuityId, 0.5),
            Signal("semantic.credential_request", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        Assert.Equal(0.4, risk.Index, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 5);
        Assert.Contains(risk.Masked, m =>
            m.SignalId == SemanticDimensions.ConversationalContinuityId && m.Reason is not null);
    }

    /// <summary>
    /// The boundary of decision 32's exclusion, and the reason it is a policy list rather than a
    /// value test. A measured semantic zero is still a measurement and still counts: it is the
    /// statement "this dimension was asked and the answer was no", and dropping it would be decision
    /// 23's hazard in the other direction, unmasking an outage into silence. Only a dimension named
    /// as excluded by policy leaves the arithmetic, and it leaves on its identity, not on its value.
    /// </summary>
    [Fact]
    public void The_policy_exclusion_reaches_only_the_excluded_dimension()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.0),
            Signal("semantic.unsolicited_solicitation", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        Assert.Equal(0.2, risk.Index, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeightFraction);
        Assert.Empty(risk.Masked);
    }

    /// <summary>
    /// The scope of the reason, which is the half of this ruling that a careless reading gets wrong.
    /// The policy reason belongs to the case the ruling is about: a row that <b>carried an answer</b>
    /// and was excluded anyway. When nothing reported on continuity at all, the row is still masked,
    /// and it still carries no policy reason, because "the question was never answered" and "policy
    /// excludes this dimension" are two different statements and an audit must be able to tell them
    /// apart. Making the reason unconditional would have told a reader that a ruling had been made
    /// about an answer that does not exist.
    /// </summary>
    [Fact]
    public void An_absent_continuity_row_is_masked_without_a_policy_reason()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        var masked = Assert.Single(risk.Masked);
        Assert.Equal(SemanticDimensions.ConversationalContinuityId, masked.SignalId);

        // The row was never reported, and the availability is the whole explanation.
        Assert.Equal(EvidenceAvailability.Unavailable, masked.Availability);
        Assert.Null(masked.Reason);

        // Excluded all the same: the reason is scoped, the exclusion is not.
        Assert.Equal(0.4, risk.Index, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 5);
    }

    /// <summary>
    /// The same scope, on the shape the corpus actually produces rather than the shape a test
    /// invents: with no conversation, the classifier reports continuity <c>NotApplicable</c> (not
    /// absent), and that row must carry no policy reason either. This is the arm-0 and arm-3 case,
    /// and it is what makes the distinction checkable from the response rather than only from here.
    /// </summary>
    [Fact]
    public void A_not_applicable_continuity_row_is_masked_without_a_policy_reason()
    {
        var evidence = new[]
        {
            new Evidence
            {
                SignalId = SemanticDimensions.ConversationalContinuityId,
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.NotApplicable,
                Value = null,
                SourceVersion = "jev-1.13.0",
                ObservedAt = Now,
            },
            Signal("semantic.credential_request", 0.4),
        };

        var risk = CompositeRiskScorer.Compute(evidence, ContinuityWeights);

        var masked = Assert.Single(risk.Masked);
        Assert.Equal(EvidenceAvailability.NotApplicable, masked.Availability);
        Assert.Null(masked.Reason);

        // Still out of both denominators, exactly as the available case is. What differs is the
        // explanation, which is the availability and not a ruling.
        Assert.Equal(0.4, risk.Index, precision: 5);
        Assert.Equal(1.0, risk.CoveredWeightFraction, precision: 5);
    }

    /// <summary>
    /// The two directions land on the same denominator, which is what makes the exclusion a rule
    /// about the dimension rather than about the answer. If A and B produced different denominators,
    /// a consumer checking an index from its own body would have to know which direction it was
    /// looking at before it could divide, and the published denominator would not be sufficient.
    /// </summary>
    /// <remarks>
    /// Decision 41 is why this belongs here and nowhere else. The covered fraction reaches no client
    /// (the response's coverage member is the message's eight booleans, not this fraction), and the
    /// change decision 32 makes to it, 0.9359 to 1.0 on a fully measured arm, is invisible through
    /// every route because both values sit above both shipped floors. So this test is the only place
    /// the change can be observed to happen at all, which is exactly what decision 41 asks for.
    /// </remarks>
    [Fact]
    public void The_denominator_is_the_same_whichever_way_continuity_answers()
    {
        var confirming = CompositeRiskScorer.Compute(
            [
                Signal(SemanticDimensions.ConversationalContinuityId, 1.0),
                Signal("semantic.credential_request", 0.4),
            ],
            ContinuityWeights);

        var disconfirming = CompositeRiskScorer.Compute(
            [
                Signal(SemanticDimensions.ConversationalContinuityId, 0.0),
                Signal("semantic.credential_request", 0.4),
            ],
            ContinuityWeights);

        Assert.Equal(confirming.CoveredWeight, disconfirming.CoveredWeight);
        Assert.Equal(confirming.CoveredWeightFraction, disconfirming.CoveredWeightFraction);
        Assert.Equal(confirming.Index, disconfirming.Index);

        // And the fully measured arm reads 1.0, not the 7.3/7.8 a client summing the served rows
        // where counted over all rows would compute. Both candidate values clear both shipped floors
        // (MinimumCoverageForAllow 0.30, MinimumCoverageForIrreversibleAction 0.60), which is why no
        // gate moves and why this assertion is the only place the difference can be caught.
        Assert.Equal(1.0, disconfirming.CoveredWeightFraction, precision: 12);
        Assert.Equal(1.0, confirming.CoveredWeightFraction, precision: 12);
    }

    [Fact]
    public void Correlated_dimensions_are_summed_with_weights_not_multiplied()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.8),
            Signal("semantic.unsolicited_solicitation", 0.8),
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        // A weighted mean stays within 0..1. Multiplying the two would give 0.64 and pretend the
        // dimensions were independent, compounding one underlying signal into false certainty.
        Assert.Equal(0.8, risk.Index, precision: 5);
        Assert.True(risk.Index <= 1.0);
    }

    private static PolicyOptions Options() => new()
    {
        HoldWindow = TimeSpan.FromSeconds(5),
        MaxHoldDeadline = TimeSpan.FromSeconds(30),
        DimensionWeights = Weights,
    };

    private static PolicyDecision Decide(
        RiskIndexResult risk,
        PolicyContext context,
        MailDirection direction = MailDirection.Inbound,
        PolicyOptions? options = null,
        IReadOnlyList<Evidence>? evidence = null)
    {
        var engine = new MailPolicyEngine(options ?? Options(), new FixedTimeProvider(Now));
        return engine.Decide(new PolicyInput
        {
            Evidence = evidence ?? [],
            Risk = risk,
            Context = context,
            Direction = direction,
        });
    }

    private static PolicyContext Context() => new()
    {
        EmergencyKillSwitchEngaged = false,
        OutboundQuotaExhausted = false,
        VerifiedSecurityRuleViolations = [],
        AllowlistEntryValid = false,
        BaselineFrozenForSuspectedCompromise = false,
    };

    // `coveredWeight` exists so the fixture states the denominator the scorer would have produced
    // rather than letting it default. Nothing in the policy engine reads it: it travels to the
    // response so a consumer can check the index (decision 37), and these tests are about the
    // action the index produces, not about the arithmetic behind it.
    private static RiskIndexResult Risk(
        double index,
        double coverage,
        IReadOnlyList<Evidence>? evidence = null,
        double coveredWeight = 1.0)
        => new()
        {
            Index = index,
            CoveredWeightFraction = coverage,
            CoveredWeight = coveredWeight,
            Masked = [],
            ContributingSignalIds = evidence?.Select(e => e.SignalId).ToList() ?? [],
        };

    /// <summary>
    /// Renders the <c>evidence.masked_dimensions</c> reason for a message whose only masked rows are
    /// the ones given. Coverage is full and one deterministic row is measured, so the decision is an
    /// Allow and the surface is read on the calm path rather than argued from a hold.
    /// </summary>
    private static string MaskedReason(IReadOnlyList<MaskedDimension> masked)
    {
        var risk = new RiskIndexResult
        {
            Index = 0.0,
            CoveredWeightFraction = 1.0,
            CoveredWeight = 1.0,
            Masked = masked,
            ContributingSignalIds = [],
        };

        var decision = Decide(risk, Context(), evidence: [Deterministic()]);
        return decision.Reasons.Single(r => r.Code == "evidence.masked_dimensions").Message;
    }

    private static Evidence Signal(string id, double value, EvidenceOrigin origin = EvidenceOrigin.Semantic) => new()
    {
        SignalId = id,
        Origin = origin,
        Availability = EvidenceAvailability.Available,
        Value = value,
        Confidence = null,
        SourceVersion = origin == EvidenceOrigin.Semantic ? "jev-1.13.0" : "behavioural-1.0.0",
        ObservedAt = Now,
    };

    /// <summary>
    /// A measured signal from a non-probabilistic producer, and the only kind that can corroborate a
    /// model's calm.
    /// </summary>
    /// <remarks>
    /// The two clauses are not the same claim and the distinction is load-bearing: deterministic
    /// origin is a producer's stamp ("a fact, not a model opinion"), while corroboration needs a
    /// measurement. Where the two come apart the availability decides, which is what the gate reads,
    /// and <see cref="A_deterministic_row_that_is_unavailable_does_not_corroborate"/> pins it.
    /// </remarks>
    private static Evidence Deterministic(string id = "headers.authentication_summary", double value = 0.0) => new()
    {
        SignalId = id,
        Origin = EvidenceOrigin.Deterministic,
        Availability = EvidenceAvailability.Available,
        Value = value,
        Confidence = null,
        SourceVersion = "mime-1.0.0",
        ObservedAt = Now,
    };

    /// <summary>
    /// The eleven counted semantic rows in one availability: how the pipeline reports a provider that
    /// was asked and did not answer (<c>Unavailable</c>), and how it reports questions that were
    /// never part of this message's set (<c>NotApplicable</c>, from the classifier's own partition).
    /// Continuity is left out because it is excluded by policy in both directions (decision 32).
    /// </summary>
    private static Evidence[] SemanticBackbone(EvidenceAvailability availability) =>
        SemanticDimensions.All
            .Where(dimension => dimension.Id != SemanticDimensions.ConversationalContinuityId)
            .Select(dimension => new Evidence
            {
                SignalId = dimension.Id,
                Origin = EvidenceOrigin.Semantic,
                Availability = availability,
                Value = null,
                Confidence = null,
                SourceVersion = "jev-1.13.0",
                ObservedAt = Now,
            })
            .ToArray();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
