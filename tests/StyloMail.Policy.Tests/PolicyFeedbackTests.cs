namespace StyloMail.Policy.Tests;

/// <summary>
/// The feedback loop's smallest working slice: capture, label comparison, shadow mode, the promotion
/// gate and retirement.
/// </summary>
/// <remarks>
/// The gates are asserted on their refusal codes, not only on <c>Approved</c>. A candidate refused
/// for an unrelated reason would otherwise read as evidence that the gate under test worked.
/// </remarks>
public sealed class PolicyFeedbackTests
{
    [Fact]
    public void A_captured_decision_keeps_its_evidence_and_the_rule_version_that_produced_it()
    {
        var evaluation = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.90)]));

        var capture = DecisionCapture.FromEvaluation("decision-1", evaluation);

        Assert.Equal("decision-1", capture.DecisionId);
        Assert.Equal(evaluation.TenantId, capture.TenantId);
        Assert.Equal(evaluation.PackId, capture.PackId);
        Assert.Equal(evaluation.PackVersion, capture.PackVersion);
        Assert.Equal(evaluation.RuleSetVersion, capture.RuleSetVersion);
        Assert.Equal("rule-payment-redirection", capture.RuleId);
        Assert.Equal(1, capture.RuleVersion);
        Assert.Equal(PolicyVerdict.Quarantine, capture.Verdict);
        Assert.Equal(evaluation.EvidenceSnapshotDigest, capture.EvidenceSnapshotDigest);
        Assert.Same(evaluation.EvidenceSnapshot, capture.EvidenceSnapshot);
    }

    [Fact]
    public void A_human_and_model_label_that_disagree_become_a_review_item()
    {
        var report = LabelAgreement.Compare(
            [new HumanReview
            {
                DecisionId = "decision-1",
                Outcome = ReviewOutcome.Overridden,
                Label = FeedbackLabelKind.Benign,
                Actor = "operator-a",
                RecordedAt = PolicyFixtures.Now,
            }],
            [new ModelLabel
            {
                DecisionId = "decision-1",
                Label = FeedbackLabelKind.Suspicious,
                ModelId = "llm-decision",
                ModelVersion = "2026-10",
                PromptContractVersion = "label-prompt/3",
                RecordedAt = PolicyFixtures.Now,
            }]);

        Assert.Equal(1, report.Compared);
        Assert.Equal(0, report.Agreed);
        var item = Assert.Single(report.Disagreements);
        Assert.Equal(FeedbackLabelKind.Benign, item.HumanLabel);
        Assert.Equal(FeedbackLabelKind.Suspicious, item.ModelLabel);
        Assert.Equal("llm-decision", item.ModelId);
        Assert.Equal("2026-10", item.ModelVersion);
        Assert.Equal("label-prompt/3", item.PromptContractVersion);
        Assert.Equal(LabelReviewItem.ReasonCode, "policy.label_disagreement");
    }

    [Fact]
    public void An_agreeing_label_is_counted_and_is_not_a_review_item()
    {
        var report = LabelAgreement.Compare(
            [new HumanReview
            {
                DecisionId = "decision-1",
                Outcome = ReviewOutcome.Confirmed,
                Label = FeedbackLabelKind.Suspicious,
                Actor = "operator-a",
                RecordedAt = PolicyFixtures.Now,
            }],
            [new ModelLabel
            {
                DecisionId = "decision-1",
                Label = FeedbackLabelKind.Suspicious,
                ModelId = "llm-decision",
                ModelVersion = "2026-10",
                PromptContractVersion = "label-prompt/3",
                RecordedAt = PolicyFixtures.Now,
            }]);

        Assert.Equal(1, report.Compared);
        Assert.Equal(1, report.Agreed);
        Assert.Empty(report.Disagreements);
    }

    [Fact]
    public void A_confirmation_without_a_label_is_not_a_benign_label()
    {
        var report = LabelAgreement.Compare(
            [new HumanReview
            {
                DecisionId = "decision-1",
                Outcome = ReviewOutcome.Confirmed,
                Actor = "operator-a",
                RecordedAt = PolicyFixtures.Now,
            }],
            [new ModelLabel
            {
                DecisionId = "decision-1",
                Label = FeedbackLabelKind.Suspicious,
                ModelId = "llm-decision",
                ModelVersion = "2026-10",
                PromptContractVersion = "label-prompt/3",
                RecordedAt = PolicyFixtures.Now,
            }]);

        // Confirming the action is not the same as labelling the message benign. Inferring a label
        // from the absence of an override is exactly what the feedback rules forbid.
        Assert.Equal(0, report.Compared);
        Assert.Empty(report.Disagreements);
    }

    [Fact]
    public void A_model_label_with_no_human_counterpart_is_not_a_disagreement()
    {
        var report = LabelAgreement.Compare(
            [],
            [new ModelLabel
            {
                DecisionId = "decision-1",
                Label = FeedbackLabelKind.Suspicious,
                ModelId = "llm-decision",
                ModelVersion = "2026-10",
                PromptContractVersion = "label-prompt/3",
                RecordedAt = PolicyFixtures.Now,
            }]);

        Assert.Equal(0, report.Compared);
        Assert.Empty(report.Disagreements);
    }

    [Fact]
    public void Shadow_mode_records_the_candidate_decision_without_changing_production()
    {
        var pack = PolicyFixtures.Pack();
        var production = PolicyFixtures.Evaluate(PolicyFixtures.Input(evidence:
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)]));

        var candidateRule = PolicyFixtures.PaymentRule(version: 2, bands:
            [new RuleBand(0.05, PolicyVerdict.Quarantine, "policy.candidate_quarantine")]);

        var shadow = new ShadowPolicyRun(
            pack,
            PolicyFixtures.RuleSet(candidateRule),
            new DeterministicPolicyEvaluator(new PolicyFixtures.FixedTimeProvider()));

        var record = shadow.Record(
            "decision-1",
            production,
            [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)]);

        Assert.Equal(PolicyVerdict.Permit, production.Verdict);
        Assert.Equal(PolicyVerdict.Permit, record.ProductionVerdict);
        Assert.Equal(PolicyVerdict.Quarantine, record.CandidateVerdict);
        Assert.False(record.Agrees);
        Assert.False(record.Applied);
        Assert.Equal("rule-payment-redirection", record.CandidateRuleId);
        Assert.Equal(2, record.CandidateRuleVersion);
    }

    [Fact]
    public void Shadow_agreement_accumulates_over_cases()
    {
        var pack = PolicyFixtures.Pack();
        var candidateRule = PolicyFixtures.PaymentRule(version: 2, bands:
            [new RuleBand(0.05, PolicyVerdict.Observe, "policy.candidate_observe")]);

        var shadow = new ShadowPolicyRun(
            pack,
            PolicyFixtures.RuleSet(candidateRule),
            new DeterministicPolicyEvaluator(new PolicyFixtures.FixedTimeProvider()));

        var disagreeing = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)]));
        var agreeing = PolicyFixtures.Evaluate(PolicyFixtures.Input(
            evidence: [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.01)]));

        shadow.Record("decision-1", disagreeing, [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.10)]);
        shadow.Record("decision-2", agreeing, [PolicyFixtures.Signal(PolicyFixtures.PaymentSignal, 0.01)]);

        var ledger = shadow.Snapshot();

        Assert.Equal(2, ledger.Samples);
        Assert.Equal(1, ledger.Agreements);
        Assert.Equal(0.5, ledger.AgreementRate);
    }

    [Fact]
    public void A_complete_candidate_is_promoted()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(),
            Evidence = PolicyFixtures.PromotionEvidence(),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.True(decision.Approved, string.Join("; ", decision.Failures.Select(failure => $"{failure.Code}: {failure.Message}")));
        Assert.Empty(decision.Failures);
        Assert.Contains("approver-b", decision.Summary);
    }

    [Fact]
    public void Weak_model_labels_never_satisfy_the_human_sample_gate()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(),
            Evidence = PolicyFixtures.PromotionEvidence(samples: 500, humanConfirmed: 0, modelLabeled: 500),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        var failure = Assert.Single(decision.Failures, failure => failure.Code == RulePromotionGate.HumanConfirmedBelowMinimum);
        Assert.Contains("model labels do not count", failure.Message);
    }

    [Fact]
    public void A_candidate_scored_on_its_training_window_is_refused()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(
                training: new LabelWindow { From = PolicyFixtures.Now.AddDays(-60), To = PolicyFixtures.Now.AddDays(-10) },
                heldOut: new LabelWindow { From = PolicyFixtures.Now.AddDays(-40), To = PolicyFixtures.Now.AddDays(-20) }),
            Evidence = PolicyFixtures.PromotionEvidence(),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.HeldOutOverlapsTraining);
    }

    [Fact]
    public void A_candidate_without_shadow_evidence_is_refused()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(),
            Evidence = PolicyFixtures.PromotionEvidence(shadowSamples: 0, shadowAgreement: 0.95),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.ShadowSamplesBelowMinimum);
    }

    [Fact]
    public void A_cross_pack_candidate_needs_validation_on_the_target_pack()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(sourcePackId: "pack-dating"),
            Evidence = PolicyFixtures.PromotionEvidence(targetPackId: PolicyFixtures.PackId),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.CrossPackNotValidated);
    }

    [Fact]
    public void A_cross_pack_candidate_with_target_pack_validation_is_accepted()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(sourcePackId: "pack-dating"),
            Evidence = PolicyFixtures.PromotionEvidence(
                targetPackId: PolicyFixtures.PackId,
                crossPackValidated: true,
                crossPackValidationSamples: 40),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.True(decision.Approved, string.Join("; ", decision.Failures.Select(failure => failure.Code)));
    }

    [Fact]
    public void A_candidate_needs_a_named_approver()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(),
            Evidence = PolicyFixtures.PromotionEvidence(),
            Approver = " ",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.ApproverMissing);
    }

    [Fact]
    public void An_error_budget_overrun_is_refused()
    {
        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(),
            Evidence = PolicyFixtures.PromotionEvidence(precision: 0.70, falsePositiveRate: 0.20),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.PrecisionBelowMinimum);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.FalsePositiveRateAboveBudget);
    }

    [Fact]
    public void An_invalid_candidate_rule_is_refused_before_its_statistics_are_considered()
    {
        var invalid = PolicyFixtures.PaymentRule(version: 2) with { Category = "vibes" };

        var request = new PromotionRequest
        {
            Candidate = PolicyFixtures.Candidate(rule: invalid),
            Evidence = PolicyFixtures.PromotionEvidence(),
            Approver = "approver-b",
        };

        var decision = RulePromotionGate.Evaluate(request);

        Assert.False(decision.Approved);
        Assert.Contains(decision.Failures, failure => failure.Code == RulePromotionGate.RuleInvalid);
    }

    [Fact]
    public void A_rule_is_suspended_when_its_override_budget_is_crossed()
    {
        var assessment = RuleRetirementMonitor.Assess(new RuleOutcomeWindow
        {
            RuleId = "rule-payment-redirection",
            RuleVersion = 1,
            Evaluations = 100,
            Overrides = 6,
            Reversals = 0,
        });

        Assert.True(assessment.Assessed);
        Assert.Equal(RuleStatus.SuspendedPendingReview, assessment.Status);
        Assert.Contains(RuleRetirementMonitor.SuspendedForOverrides, assessment.Reasons);
        Assert.Equal(0.06, assessment.OverrideRate);
    }

    [Fact]
    public void A_rule_is_suspended_when_its_reversal_budget_is_crossed()
    {
        var assessment = RuleRetirementMonitor.Assess(new RuleOutcomeWindow
        {
            RuleId = "rule-payment-redirection",
            RuleVersion = 1,
            Evaluations = 100,
            Overrides = 1,
            Reversals = 3,
        });

        Assert.True(assessment.Assessed);
        Assert.Equal(RuleStatus.SuspendedPendingReview, assessment.Status);
        Assert.Contains(RuleRetirementMonitor.SuspendedForReversals, assessment.Reasons);
    }

    [Fact]
    public void A_rule_inside_its_budget_stays_live()
    {
        var assessment = RuleRetirementMonitor.Assess(new RuleOutcomeWindow
        {
            RuleId = "rule-payment-redirection",
            RuleVersion = 1,
            Evaluations = 100,
            Overrides = 4,
            Reversals = 1,
        });

        Assert.True(assessment.Assessed);
        Assert.Equal(RuleStatus.Live, assessment.Status);
        Assert.Empty(assessment.Reasons);
    }

    [Fact]
    public void A_thin_window_is_not_assessed_rather_than_cleared()
    {
        var assessment = RuleRetirementMonitor.Assess(new RuleOutcomeWindow
        {
            RuleId = "rule-payment-redirection",
            RuleVersion = 1,
            Evaluations = 10,
            Overrides = 5,
            Reversals = 2,
        });

        Assert.False(assessment.Assessed);
        Assert.Equal(RuleStatus.Live, assessment.Status);
        Assert.Contains(RuleRetirementMonitor.NotAssessed, assessment.Reasons);
    }

    [Fact]
    public void A_retirement_window_cannot_hold_more_overrides_than_evaluations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleRetirementMonitor.Assess(new RuleOutcomeWindow
        {
            RuleId = "rule-payment-redirection",
            RuleVersion = 1,
            Evaluations = 10,
            Overrides = 11,
            Reversals = 0,
        }));
    }
}
