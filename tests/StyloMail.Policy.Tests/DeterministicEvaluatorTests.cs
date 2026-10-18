using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// The evaluator's contract: same inputs, same decision; every outcome explained; missing evidence
/// escalates rather than scoring zero.
/// </summary>
/// <remarks>
/// These tests use a fixed clock so a decision's <c>EvaluatedAt</c> is part of the determinism claim
/// rather than an uncontrolled difference between two runs.
/// </remarks>
public sealed class DeterministicEvaluatorTests
{
    [Fact]
    public void The_same_evidence_and_versions_produce_the_same_decision()
    {
        var input = PolicyFixtures.Input(evidence:
        [
            PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.72),
            PolicyFixtures.Signal(PolicyFixtures.CredentialSignal, 0.10),
        ]);

        var first = PolicyFixtures.Evaluate(input);

        // A different list order is the same set of observations. If the decision moved, the
        // decision would be a property of the caller's ordering rather than of the evidence.
        var second = PolicyFixtures.Evaluate(input with { Evidence = input.Evidence.Reverse().ToList() });

        Assert.Equal(PolicyVerdict.Hold, first.Verdict);
        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.DecidingRuleId, second.DecidingRuleId);
        Assert.Equal(first.DecidingRuleVersion, second.DecidingRuleVersion);
        Assert.Equal(first.DecidingThreshold, second.DecidingThreshold);
        Assert.Equal(first.EvidenceSnapshotDigest, second.EvidenceSnapshotDigest);
        Assert.Equal(first.Coverage, second.Coverage);
        Assert.Equal(first.EvaluatedAt, second.EvaluatedAt);
        Assert.Equal(
            first.Rules.Select(rule => (rule.RuleId, rule.State, rule.Outcome, rule.Threshold)),
            second.Rules.Select(rule => (rule.RuleId, rule.State, rule.Outcome, rule.Threshold)));
    }

    [Fact]
    public void The_decision_names_the_rule_version_threshold_and_evidence_snapshot()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));

        Assert.Equal(PolicyVerdict.Quarantine, evaluation.Verdict);
        Assert.Equal("rule-payment-redirection", evaluation.DecidingRuleId);
        Assert.Equal(1, evaluation.DecidingRuleVersion);
        Assert.Equal(0.80, evaluation.DecidingThreshold);
        Assert.Equal("policy.payment_redirection_quarantine", evaluation.DecidingReasonCode);
        Assert.Equal(DeterministicPolicyEvaluator.DecidedBy, evaluation.DecidedBy);
        Assert.Equal("1.0.0", evaluation.PackVersion);
        Assert.Equal(PolicyFixtures.RuleSetVersion, evaluation.RuleSetVersion);
        Assert.Equal(PolicyFixtures.ScorerVersion, evaluation.ScorerVersion);

        var snapshot = Assert.Single(evaluation.EvidenceSnapshot);
        Assert.Equal(PolicyFixtures.PaymentSignal, snapshot.SignalId);
        Assert.Equal(PolicyFixtures.ScorerVersion, snapshot.SourceVersion);
        Assert.Equal(64, evaluation.EvidenceSnapshotDigest.Length);
    }

    [Fact]
    public void A_changed_value_changes_the_snapshot_digest()
    {
        var baseline = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));
        var changed = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.91)]));

        Assert.NotEqual(baseline.EvidenceSnapshotDigest, changed.EvidenceSnapshotDigest);
    }

    [Fact]
    public void A_missing_dimension_escalates_rather_than_scoring_zero()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(evidence: []));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.True(evaluation.RequiresHumanReview);
        Assert.Equal("policy.evidence_missing", evaluation.DecidingReasonCode);
        Assert.Contains(PolicyFixtures.PaymentSignal, evaluation.MissingDimensions);

        var rule = Assert.Single(evaluation.Rules);
        Assert.Equal(RuleEvaluationState.MissingEvidence, rule.State);
        Assert.Null(rule.ObservedValue);
        Assert.Equal(0, rule.Coverage);
    }

    [Fact]
    public void An_unavailable_row_is_missing_evidence_not_a_measured_zero()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Unavailable(PolicyFixtures.PaymentSignal)]));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal(RuleEvaluationState.MissingEvidence, evaluation.Rules[0].State);
        Assert.Null(evaluation.Rules[0].ObservedValue);
    }

    [Fact]
    public void A_gray_zone_score_escalates_to_review()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.40)]));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.True(evaluation.RequiresHumanReview);
        Assert.Equal(0.35, evaluation.DecidingThreshold);
        Assert.Equal("policy.payment_redirection_review", evaluation.DecidingReasonCode);
    }

    [Fact]
    public void A_zero_tolerance_candidate_escalates_without_accusing()
    {
        var levels = PolicyFixtures.DefaultLevels();
        levels[PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.ZeroTolerance;

        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            pack: PolicyFixtures.Pack(levels: levels),
            ruleSet: PolicyFixtures.RuleSet(PolicyFixtures.GroomingRule()),
            evidence: [PolicyFixtures.Signal("semantic.grooming", 0.90)]));

        // The band itself says Observe. Zero tolerance raises the response to a human, and the
        // outcome is review rather than a hold or a block: it is not an accusation.
        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.True(evaluation.RequiresHumanReview);
        Assert.False(evaluation.Rules[0].CappedByCeiling);
        Assert.Contains("zero tolerance", evaluation.DecidingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_pack_ceiling_caps_a_fired_outcome_and_says_so()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            pack: PolicyFixtures.Pack(ceiling: PolicyVerdict.Hold),
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));

        Assert.Equal(PolicyVerdict.Hold, evaluation.Verdict);
        Assert.True(evaluation.Rules[0].CappedByCeiling);
        Assert.Equal(0.80, evaluation.DecidingThreshold);
    }

    [Fact]
    public void The_ceiling_does_not_reduce_a_missing_evidence_escalation()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            pack: PolicyFixtures.Pack(ceiling: PolicyVerdict.Hold),
            evidence: []));

        // The ceiling bounds automatic actions, not the duty to look.
        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.True(evaluation.RequiresHumanReview);
        Assert.False(evaluation.Rules[0].CappedByCeiling);
    }

    [Fact]
    public void A_thin_sample_escalates_instead_of_deciding()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet(PolicyFixtures.PaymentRule(minimumSample: 10)),
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90, sampleSupport: 3)]));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal(RuleEvaluationState.InsufficientSample, evaluation.Rules[0].State);
        Assert.Equal("policy.sample_below_minimum", evaluation.Rules[0].ReasonCode);
        Assert.Equal(3, evaluation.Rules[0].SampleSupport);
    }

    [Fact]
    public void Conflicting_evidence_escalates_rather_than_picking_a_row()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence:
            [
                PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90, sourceVersion: "producer-a/1"),
                PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10, sourceVersion: "producer-b/1"),
            ]));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal(RuleEvaluationState.ConflictingEvidence, evaluation.Rules[0].State);
        Assert.Equal("policy.conflicting_evidence", evaluation.Rules[0].ReasonCode);
        Assert.Equal(PolicyFixtures.PaymentSignal, Assert.Single(evaluation.Rules[0].ConflictingSignals));
    }

    [Fact]
    public void A_value_below_the_detection_floor_is_recorded_as_not_fired()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)]));

        Assert.Equal(PolicyVerdict.Permit, evaluation.Verdict);
        Assert.False(evaluation.RequiresHumanReview);
        Assert.Null(evaluation.DecidingRuleId);
        Assert.Equal("policy.no_rule_fired", evaluation.DecidingReasonCode);
        Assert.Equal(RuleEvaluationState.NotFired, evaluation.Rules[0].State);
    }

    [Fact]
    public void A_quiet_rule_is_still_named_in_the_explanation()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet(
                PolicyFixtures.PaymentRule(),
                PolicyFixtures.CredentialRule()),
            evidence:
            [
                PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90),
                PolicyFixtures.Signal(PolicyFixtures.CredentialSignal, 0.00),
            ]));

        Assert.Equal(2, evaluation.Rules.Count);
        Assert.Equal(
            RuleEvaluationState.Fired,
            evaluation.Rules.Single(rule => rule.RuleId == "rule-payment-redirection").State);
        Assert.Equal(
            RuleEvaluationState.NotFired,
            evaluation.Rules.Single(rule => rule.RuleId == "rule-credential-request").State);
    }

    [Fact]
    public void A_rule_set_version_mismatch_is_refused()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet() with { Version = "rules/2" }));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal("policy.rule_set_version_mismatch", evaluation.DecidingReasonCode);
        Assert.Empty(evaluation.Rules);
    }

    [Fact]
    public void An_unvalidated_pack_is_refused_before_it_can_evaluate()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            pack: PolicyFixtures.Pack(ceiling: PolicyVerdict.Permit)));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal("policy.pack_invalid", evaluation.DecidingReasonCode);
        Assert.Contains(PolicyPackValidator.ActionCeilingInvalid, evaluation.DecidingReason);
    }

    [Fact]
    public void A_cross_tenant_pack_is_refused()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(tenant: "tenant-b"));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal("policy.tenant_scope_mismatch", evaluation.DecidingReasonCode);
        Assert.True(evaluation.RequiresHumanReview);
    }

    [Fact]
    public void An_empty_rule_set_escalates()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: new PolicyRuleSet { Id = "empty", Version = PolicyFixtures.RuleSetVersion, Rules = [] }));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal("policy.no_rules", evaluation.DecidingReasonCode);
    }

    [Fact]
    public void An_invalid_rule_set_is_refused()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet(
                PolicyFixtures.PaymentRule(),
                PolicyFixtures.PaymentRule(version: 2))));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal("policy.rule_set_invalid", evaluation.DecidingReasonCode);
        Assert.Contains(RuleSetValidator.DuplicateRuleId, evaluation.DecidingReason);
    }

    [Fact]
    public void A_missing_evidence_outcome_cannot_fail_open()
    {
        var rule = PolicyFixtures.PaymentRule(onMissing: PolicyVerdict.Observe);

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.MissingEvidenceNotEscalating);
    }

    [Fact]
    public void A_rule_that_cannot_name_its_inputs_is_refused()
    {
        var rule = PolicyFixtures.PaymentRule() with
        {
            RequiredSignalIds = [],
            SignalId = string.Empty,
        };

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.SignalMissing);
    }

    [Fact]
    public void A_band_that_returns_permit_cannot_be_told_apart_from_no_match()
    {
        var rule = PolicyFixtures.PaymentRule(bands:
        [
            new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.quarantine"),
            new RuleBand(0.50, PolicyVerdict.Permit, "policy.permit"),
        ]);

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.BandOutcomeNotActionable);
    }

    [Fact]
    public void Two_bands_at_one_threshold_make_the_outcome_depend_on_order()
    {
        var rule = PolicyFixtures.PaymentRule(bands:
        [
            new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.quarantine"),
            new RuleBand(0.80, PolicyVerdict.Hold, "policy.hold"),
        ]);

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.ThresholdDuplicated);
    }

    [Fact]
    public void An_unknown_rule_category_is_refused()
    {
        var rule = PolicyFixtures.PaymentRule() with { Category = "vibes" };

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.CategoryUnknown);
    }

    [Fact]
    public void A_rule_refuses_to_fire_below_its_coverage_minimum()
    {
        var rule = PolicyFixtures.PaymentRule() with
        {
            CoverageSignalIds = [PolicyFixtures.PaymentSignal, PolicyFixtures.CredentialSignal],
            MinimumCoverage = 1.0,
        };

        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet(rule),
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));

        Assert.Equal(PolicyVerdict.Review, evaluation.Verdict);
        Assert.Equal(RuleEvaluationState.InsufficientCoverage, evaluation.Rules[0].State);
        Assert.Equal("policy.coverage_below_minimum", evaluation.Rules[0].ReasonCode);
        Assert.Equal(0.5, evaluation.Rules[0].Coverage);
        Assert.Equal(1, evaluation.Rules[0].CoverageSignalsMeasured);
        Assert.Equal(2, evaluation.Rules[0].CoverageSignalsExpected);
    }

    [Fact]
    public void A_rule_that_declares_it_can_answer_on_partial_coverage_fires()
    {
        // The control for the coverage test: the same missing dimension is not a defect when the
        // rule has declared, in advance, the coverage it needs.
        var rule = PolicyFixtures.PaymentRule() with
        {
            CoverageSignalIds = [PolicyFixtures.PaymentSignal, PolicyFixtures.CredentialSignal],
            MinimumCoverage = 0.5,
        };

        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            ruleSet: PolicyFixtures.RuleSet(rule),
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));

        Assert.Equal(PolicyVerdict.Quarantine, evaluation.Verdict);
        Assert.Equal(RuleEvaluationState.Fired, evaluation.Rules[0].State);
        Assert.Equal(0.5, evaluation.Rules[0].Coverage);
    }

    [Fact]
    public void A_coverage_set_cannot_be_narrower_than_the_required_inputs()
    {
        var rule = PolicyFixtures.PaymentRule() with
        {
            RequiredSignalIds = [PolicyFixtures.PaymentSignal, PolicyFixtures.CredentialSignal],
            CoverageSignalIds = [PolicyFixtures.PaymentSignal],
        };

        var violations = RuleSetValidator.Validate(PolicyFixtures.RuleSet(rule));

        Assert.Contains(violations, violation => violation.Code == RuleSetValidator.CoverageExcludesRequired);
    }

    [Fact]
    public void A_measurement_that_did_not_happen_cannot_be_read_as_a_zero()
    {
        // This is the control for the missing-dimension test: a measured 0.0 is a real answer and
        // must not take the escalation path, or every calm message would hold.
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.0)]));

        Assert.Equal(PolicyVerdict.Permit, evaluation.Verdict);
        Assert.Empty(evaluation.MissingDimensions);
        Assert.Equal(0.0, evaluation.Rules[0].ObservedValue);
    }
}
