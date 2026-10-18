using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>
/// The label a human or a model asserts about a decision. Labels are evidence about a decision, not
/// decisions themselves.
/// </summary>
public enum FeedbackLabelKind
{
    Benign = 0,
    Suspicious = 1,
    Wanted = 2,
    Unwanted = 3,
}

/// <summary>
/// The stored record of one policy decision, as the feedback loop sees it.
/// </summary>
/// <remarks>
/// Capture is a copy, not a reference to live state: a label attached to a decision must keep
/// pointing at the evidence and rule version that produced it, even after the rule is superseded or
/// the pack moves on. The snapshot digest is the join key back to the evaluation.
/// </remarks>
public sealed record DecisionCapture
{
    public required string DecisionId { get; init; }

    public required string TenantId { get; init; }

    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public required string RuleSetVersion { get; init; }

    public string? RuleId { get; init; }

    public int? RuleVersion { get; init; }

    public required PolicyVerdict Verdict { get; init; }

    public required bool RequiresHumanReview { get; init; }

    public required IReadOnlyList<string> MissingDimensions { get; init; }

    public required IReadOnlyList<StyloMail.Core.Evidence> EvidenceSnapshot { get; init; }

    public required string EvidenceSnapshotDigest { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>Copies the explanation into the durable feedback row.</summary>
    public static DecisionCapture FromEvaluation(string decisionId, PolicyEvaluation evaluation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentNullException.ThrowIfNull(evaluation);

        return new DecisionCapture
        {
            DecisionId = decisionId,
            TenantId = evaluation.TenantId,
            PackId = evaluation.PackId,
            PackVersion = evaluation.PackVersion,
            RuleSetVersion = evaluation.RuleSetVersion,
            RuleId = evaluation.DecidingRuleId,
            RuleVersion = evaluation.DecidingRuleVersion,
            Verdict = evaluation.Verdict,
            RequiresHumanReview = evaluation.RequiresHumanReview,
            MissingDimensions = evaluation.MissingDimensions,
            EvidenceSnapshot = evaluation.EvidenceSnapshot,
            EvidenceSnapshotDigest = evaluation.EvidenceSnapshotDigest,
            DecidedAt = evaluation.EvaluatedAt,
        };
    }
}

/// <summary>What a human did with a decision.</summary>
public enum ReviewOutcome
{
    /// <summary>The human agreed with the decision.</summary>
    Confirmed = 0,

    /// <summary>The human chose a different action.</summary>
    Overridden = 1,

    /// <summary>A later event showed the decision was wrong, e.g. a delivered message was reported.</summary>
    Reversed = 2,
}

/// <summary>
/// A human's action on a decision: the strong label, carrying who asserted it and when.
/// </summary>
/// <remarks>
/// A label is optional. "Confirmed" without a label says the action was right; it does not claim the
/// message was benign, and treating the absence of an override as a benign label is exactly the
/// inference spec 9.14 forbids ("a human confirmation is recorded as a distinct event, not inferred
/// from the absence of an override").
/// </remarks>
public sealed record HumanReview
{
    public required string DecisionId { get; init; }

    public required ReviewOutcome Outcome { get; init; }

    public FeedbackLabelKind? Label { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public string? Rationale { get; init; }
}

/// <summary>
/// A model's label: a weak label with its model, version and prompt contract recorded.
/// </summary>
/// <remarks>
/// This record is deliberately not interchangeable with <see cref="HumanReview"/>. A model label can
/// contribute evidence and can be compared with a human's, but it can never satisfy a promotion
/// gate on its own (spec 9.14, "feedback is attack surface").
/// </remarks>
public sealed record ModelLabel
{
    public required string DecisionId { get; init; }

    public required FeedbackLabelKind Label { get; init; }

    /// <summary>The model id, never an alias: aliases move and a label must not.</summary>
    public required string ModelId { get; init; }

    public required string ModelVersion { get; init; }

    /// <summary>The prompt contract version the label was produced under.</summary>
    public required string PromptContractVersion { get; init; }

    public double? Confidence { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>A human label and a model label that disagree. First-class, never averaged away.</summary>
public sealed record LabelReviewItem
{
    public const string ReasonCode = "policy.label_disagreement";

    public required string DecisionId { get; init; }

    public required FeedbackLabelKind HumanLabel { get; init; }

    public required FeedbackLabelKind ModelLabel { get; init; }

    public required ReviewOutcome HumanOutcome { get; init; }

    public required string HumanActor { get; init; }

    public required string ModelId { get; init; }

    public required string ModelVersion { get; init; }

    public required string PromptContractVersion { get; init; }
}

/// <summary>The result of comparing human and model labels over a set of decisions.</summary>
public sealed record LabelAgreementReport
{
    /// <summary>Disagreements, sorted by decision id. Each is a review item, not an error count.</summary>
    public required IReadOnlyList<LabelReviewItem> Disagreements { get; init; }

    public required int Compared { get; init; }

    public required int Agreed { get; init; }
}

/// <summary>
/// Compares human labels with model labels.
/// </summary>
/// <remarks>
/// Only decisions where both a human and a model supplied a label are compared. A human
/// confirmation with no label is not a benign label, and a model label with no human counterpart is
/// not a disagreement: both would manufacture signal out of missing data, which is the same failure
/// as scoring a missing dimension as zero.
/// </remarks>
public static class LabelAgreement
{
    public static LabelAgreementReport Compare(
        IEnumerable<HumanReview> humanReviews,
        IEnumerable<ModelLabel> modelLabels)
    {
        ArgumentNullException.ThrowIfNull(humanReviews);
        ArgumentNullException.ThrowIfNull(modelLabels);

        var labelsByDecision = modelLabels
            .GroupBy(label => label.DecisionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(label => label.RecordedAt).First(), StringComparer.Ordinal);

        var disagreements = new List<LabelReviewItem>();
        var compared = 0;

        foreach (var review in humanReviews
                     .Where(review => review.Label is not null)
                     .OrderBy(review => review.DecisionId, StringComparer.Ordinal))
        {
            if (!labelsByDecision.TryGetValue(review.DecisionId, out var modelLabel))
            {
                continue;
            }

            compared++;

            if (review.Label == modelLabel.Label)
            {
                continue;
            }

            disagreements.Add(new LabelReviewItem
            {
                DecisionId = review.DecisionId,
                HumanLabel = review.Label!.Value,
                ModelLabel = modelLabel.Label,
                HumanOutcome = review.Outcome,
                HumanActor = review.Actor,
                ModelId = modelLabel.ModelId,
                ModelVersion = modelLabel.ModelVersion,
                PromptContractVersion = modelLabel.PromptContractVersion,
            });
        }

        return new LabelAgreementReport
        {
            Disagreements = disagreements,
            Compared = compared,
            Agreed = compared - disagreements.Count,
        };
    }
}

/// <summary>
/// A candidate rule's outcome recorded beside production, with no side effect.
/// </summary>
/// <remarks>
/// <see cref="Applied"/> is a constant false rather than a property a caller sets: the type has no
/// action port and no executor, so shadow mode cannot execute anything even by mistake. A shadow
/// name on a method that could act would be a claim; a type that cannot act is a guarantee.
/// </remarks>
public sealed record ShadowDecision
{
    public required string DecisionId { get; init; }

    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public required PolicyVerdict ProductionVerdict { get; init; }

    public required PolicyVerdict CandidateVerdict { get; init; }

    public string? CandidateRuleId { get; init; }

    public int? CandidateRuleVersion { get; init; }

    public required bool CandidateRequiresHumanReview { get; init; }

    public required bool Agrees { get; init; }

    public required string CandidateReasonCode { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>
    /// Always false. Shadow mode records; it does not act. The setter is private so a caller cannot
    /// turn a shadow record into a claim that something was applied.
    /// </summary>
    public bool Applied { get; private init; }
}

/// <summary>Agreement over a run of shadow cases.</summary>
public sealed record ShadowLedger
{
    public required int Samples { get; init; }

    public required int Agreements { get; init; }

    public double AgreementRate => Samples == 0 ? 0 : (double)Agreements / Samples;
}

/// <summary>
/// Runs a candidate rule set beside production and records what it would have decided.
/// </summary>
/// <remarks>
/// The candidate pack and rules are fixed at construction. <see cref="Record"/> evaluates the same
/// evidence through the candidate and returns the comparison; it does not touch the production
/// decision, and the class holds no dependency that could apply one. Promotion requires this
/// evidence to be measured over real cases before a rule goes live (spec 9.14, step 4).
/// </remarks>
public sealed class ShadowPolicyRun
{
    private readonly DeterministicPolicyEvaluator _evaluator;
    private readonly PolicyPack _pack;
    private readonly PolicyRuleSet _candidateRuleSet;
    private readonly object _gate = new();
    private int _samples;
    private int _agreements;

    public ShadowPolicyRun(
        PolicyPack candidatePack,
        PolicyRuleSet candidateRuleSet,
        DeterministicPolicyEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(candidatePack);
        ArgumentNullException.ThrowIfNull(candidateRuleSet);

        _pack = candidatePack;
        _candidateRuleSet = candidateRuleSet;
        _evaluator = evaluator ?? new DeterministicPolicyEvaluator();
    }

    public ShadowDecision Record(
        string decisionId,
        PolicyEvaluation production,
        IReadOnlyList<StyloMail.Core.Evidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(evidence);

        var candidate = _evaluator.Evaluate(new PolicyEvaluationInput
        {
            TenantId = _pack.TenantId,
            Pack = _pack,
            RuleSet = _candidateRuleSet,
            Evidence = evidence,
        });

        var agrees = candidate.Verdict == production.Verdict;

        lock (_gate)
        {
            _samples++;
            if (agrees)
            {
                _agreements++;
            }
        }

        return new ShadowDecision
        {
            DecisionId = decisionId,
            PackId = _pack.Id,
            PackVersion = _pack.Version,
            ProductionVerdict = production.Verdict,
            CandidateVerdict = candidate.Verdict,
            CandidateRuleId = candidate.DecidingRuleId,
            CandidateRuleVersion = candidate.DecidingRuleVersion,
            CandidateRequiresHumanReview = candidate.RequiresHumanReview,
            Agrees = agrees,
            CandidateReasonCode = candidate.DecidingReasonCode,
            EvaluatedAt = candidate.EvaluatedAt,
        };
    }

    public ShadowLedger Snapshot()
    {
        lock (_gate)
        {
            return new ShadowLedger { Samples = _samples, Agreements = _agreements };
        }
    }
}

/// <summary>An inclusive label window. Half-open at the end so windows cannot share a boundary case.</summary>
public sealed record LabelWindow
{
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public bool IsValid => From < To;

    public bool Overlaps(LabelWindow other) => From < other.To && other.From < To;
}

/// <summary>
/// A rule proposal distilled from labelled evidence. It is only a proposal; nothing here can act.
/// </summary>
public sealed record CandidateRule
{
    public required PolicyRule Rule { get; init; }

    /// <summary>The pack the candidate was trained on. Cross-pack promotion needs its own validation.</summary>
    public required string SourcePackId { get; init; }

    public required string Author { get; init; }

    /// <summary>The window the candidate was induced from.</summary>
    public required LabelWindow TrainingWindow { get; init; }

    /// <summary>
    /// The window held out from induction. Promotion requires this window to be non-overlapping and
    /// later than training, so a candidate cannot be scored on cases it was fitted to.
    /// </summary>
    public required LabelWindow HeldOutWindow { get; init; }
}

/// <summary>The measured evidence a promotion decision is made against.</summary>
public sealed record PromotionEvidence
{
    public required string TargetPackId { get; init; }

    public required int SampleCount { get; init; }

    /// <summary>Labels a human supplied. Model-only labels can never satisfy the sample gate.</summary>
    public required int HumanConfirmedCount { get; init; }

    public required int ModelLabeledCount { get; init; }

    public required double Precision { get; init; }

    public required double Recall { get; init; }

    public required double FalsePositiveRate { get; init; }

    public required int ShadowSamples { get; init; }

    public required double ShadowAgreement { get; init; }

    /// <summary>Model ids and versions that contributed labels. Required when any model labelled.</summary>
    public required IReadOnlyList<string> ModelVersions { get; init; }

    /// <summary>True when the candidate was validated on the target pack after cross-pack distillation.</summary>
    public bool CrossPackValidated { get; init; }

    public int CrossPackValidationSamples { get; init; }
}

/// <summary>The gates a candidate must clear. Values are policy, stated here so they are reviewable.</summary>
public sealed record PromotionGateOptions
{
    public int MinimumSamples { get; init; } = 30;

    public int MinimumHumanConfirmed { get; init; } = 10;

    public double MinimumPrecision { get; init; } = 0.9;

    public double MinimumRecall { get; init; } = 0.5;

    public double MaximumFalsePositiveRate { get; init; } = 0.1;

    public int MinimumShadowSamples { get; init; } = 20;

    public double MinimumShadowAgreement { get; init; } = 0.85;

    public int MinimumCrossPackValidationSamples { get; init; } = 30;

    public static PromotionGateOptions Default { get; } = new();
}

/// <summary>One reason a promotion was refused.</summary>
public sealed record PromotionFailure
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}

/// <summary>A promotion request: the candidate, the measured evidence and the named approver.</summary>
public sealed record PromotionRequest
{
    public required CandidateRule Candidate { get; init; }

    public required PromotionEvidence Evidence { get; init; }

    public required string Approver { get; init; }
}

/// <summary>The gate's answer. Promotion is all-or-nothing.</summary>
public sealed record PromotionDecision
{
    public required bool Approved { get; init; }

    public required IReadOnlyList<PromotionFailure> Failures { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// The promotion gate: the only route by which a candidate becomes a deterministic rule.
/// </summary>
/// <remarks>
/// <para>
/// Spec 9.14 requires a sample gate, an error gate, a held-out window, shadow evidence and a named
/// approver. Each is checked here rather than trusted from the request, and the reasons are
/// accumulated so a refusal tells the author everything that is missing.
/// </para>
/// <para>
/// <b>Weak labels are structurally insufficient.</b> The human-confirmed count has its own minimum
/// and model labels are never added to it: an LLM that labelled thirty cases has produced zero
/// human confirmations, and the gate says so rather than counting the rows.
/// </para>
/// </remarks>
public static class RulePromotionGate
{
    public const string ApproverMissing = "promotion.approver_missing";
    public const string RuleInvalid = "promotion.rule_invalid";
    public const string HeldOutWindowInvalid = "promotion.held_out_window_invalid";
    public const string HeldOutOverlapsTraining = "promotion.held_out_overlaps_training";
    public const string HeldOutNotAfterTraining = "promotion.held_out_not_after_training";
    public const string SamplesBelowMinimum = "promotion.samples_below_minimum";
    public const string HumanConfirmedBelowMinimum = "promotion.human_confirmed_below_minimum";
    public const string PrecisionBelowMinimum = "promotion.precision_below_minimum";
    public const string RecallBelowMinimum = "promotion.recall_below_minimum";
    public const string FalsePositiveRateAboveBudget = "promotion.false_positive_rate_above_budget";
    public const string ShadowSamplesBelowMinimum = "promotion.shadow_samples_below_minimum";
    public const string ShadowAgreementBelowMinimum = "promotion.shadow_agreement_below_minimum";
    public const string ModelVersionMissing = "promotion.model_version_missing";
    public const string CrossPackNotValidated = "promotion.cross_pack_not_validated";
    public const string RateInvalid = "promotion.rate_invalid";
    public const string CountsInvalid = "promotion.counts_invalid";

    public static PromotionDecision Evaluate(
        PromotionRequest request,
        PromotionGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gate = options ?? PromotionGateOptions.Default;
        var candidate = request.Candidate;
        var evidence = request.Evidence;
        var failures = new List<PromotionFailure>();

        if (string.IsNullOrWhiteSpace(request.Approver))
        {
            failures.Add(Failure(ApproverMissing, "A candidate cannot go live without a named approver."));
        }

        var ruleViolations = RuleSetValidator.Validate(new PolicyRuleSet
        {
            Id = "candidate",
            Version = "candidate",
            Rules = [candidate.Rule],
        });

        foreach (var violation in ruleViolations)
        {
            failures.Add(Failure(RuleInvalid, $"{violation.Code}: {violation.Message}"));
        }

        var training = candidate.TrainingWindow;
        var heldOut = candidate.HeldOutWindow;

        if (!training.IsValid || !heldOut.IsValid)
        {
            failures.Add(Failure(HeldOutWindowInvalid, "Training and held-out windows must both have a positive length."));
        }
        else if (heldOut.Overlaps(training))
        {
            failures.Add(Failure(HeldOutOverlapsTraining, "The held-out window overlaps the training window; the candidate was fitted to cases it would be scored on."));
        }
        else if (heldOut.From < training.To)
        {
            failures.Add(Failure(HeldOutNotAfterTraining, "The held-out window must begin after the training window ends, so the holdout is temporal."));
        }

        if (evidence.SampleCount < gate.MinimumSamples)
        {
            failures.Add(Failure(SamplesBelowMinimum, $"{evidence.SampleCount} samples is below the minimum of {gate.MinimumSamples}."));
        }

        if (evidence.HumanConfirmedCount < gate.MinimumHumanConfirmed)
        {
            failures.Add(Failure(
                HumanConfirmedBelowMinimum,
                $"{evidence.HumanConfirmedCount} human-confirmed labels is below the minimum of {gate.MinimumHumanConfirmed}. {evidence.ModelLabeledCount} model labels do not count towards it."));
        }

        if (evidence.SampleCount < evidence.HumanConfirmedCount || evidence.SampleCount < 0 || evidence.HumanConfirmedCount < 0)
        {
            failures.Add(Failure(CountsInvalid, "Label counts cannot be negative or exceed the sample count."));
        }

        CheckRate(evidence.Precision, "precision", failures);
        CheckRate(evidence.Recall, "recall", failures);
        CheckRate(evidence.FalsePositiveRate, "false-positive rate", failures);
        CheckRate(evidence.ShadowAgreement, "shadow agreement", failures);

        if (evidence.Precision < gate.MinimumPrecision)
        {
            failures.Add(Failure(PrecisionBelowMinimum, $"Precision {evidence.Precision:0.###} is below the minimum of {gate.MinimumPrecision:0.###}."));
        }

        if (evidence.Recall < gate.MinimumRecall)
        {
            failures.Add(Failure(RecallBelowMinimum, $"Recall {evidence.Recall:0.###} is below the minimum of {gate.MinimumRecall:0.###}; a rule that misses most of its category is not ready."));
        }

        if (evidence.FalsePositiveRate > gate.MaximumFalsePositiveRate)
        {
            failures.Add(Failure(FalsePositiveRateAboveBudget, $"False-positive rate {evidence.FalsePositiveRate:0.###} is above the budget of {gate.MaximumFalsePositiveRate:0.###}."));
        }

        if (evidence.ShadowSamples < gate.MinimumShadowSamples)
        {
            failures.Add(Failure(ShadowSamplesBelowMinimum, $"{evidence.ShadowSamples} shadow cases is below the minimum of {gate.MinimumShadowSamples}."));
        }

        if (evidence.ShadowAgreement < gate.MinimumShadowAgreement)
        {
            failures.Add(Failure(ShadowAgreementBelowMinimum, $"Shadow agreement {evidence.ShadowAgreement:0.###} is below the minimum of {gate.MinimumShadowAgreement:0.###}."));
        }

        if (evidence.ModelLabeledCount > 0 && evidence.ModelVersions.Count == 0)
        {
            failures.Add(Failure(ModelVersionMissing, "Model labels contributed to this candidate, so their model and version must be recorded."));
        }

        var crossPack = !string.Equals(candidate.SourcePackId, evidence.TargetPackId, StringComparison.Ordinal);
        if (crossPack && !evidence.CrossPackValidated)
        {
            failures.Add(Failure(
                CrossPackNotValidated,
                $"The candidate was distilled on '{candidate.SourcePackId}' and is being promoted to '{evidence.TargetPackId}'; a rule proven on one pack is not promoted on another without validation."));
        }
        else if (crossPack && evidence.CrossPackValidationSamples < gate.MinimumCrossPackValidationSamples)
        {
            failures.Add(Failure(
                CrossPackNotValidated,
                $"{evidence.CrossPackValidationSamples} validation samples on the target pack is below the minimum of {gate.MinimumCrossPackValidationSamples}."));
        }

        var approved = failures.Count == 0;

        return new PromotionDecision
        {
            Approved = approved,
            Failures = failures,
            Summary = approved
                ? $"Approved by {request.Approver} over {evidence.HumanConfirmedCount} human labels, precision {evidence.Precision:0.###}, shadow {evidence.ShadowSamples}."
                : $"Refused with {failures.Count} gate failure(s).",
        };
    }

    private static void CheckRate(double rate, string name, List<PromotionFailure> failures)
    {
        if (!double.IsFinite(rate) || rate is < 0 or > 1)
        {
            failures.Add(Failure(RateInvalid, $"{name} must be a fraction between 0 and 1."));
        }
    }

    private static PromotionFailure Failure(string code, string message) => new()
    {
        Code = code,
        Message = message,
    };
}

/// <summary>Observed outcomes for one live rule over a stated window, the input to retirement.</summary>
public sealed record RuleOutcomeWindow
{
    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required int Evaluations { get; init; }

    public required int Overrides { get; init; }

    public required int Reversals { get; init; }
}

/// <summary>The budget a live rule must stay inside. Crossing it suspends the rule for review.</summary>
public sealed record RetirementBudget
{
    /// <summary>Below this many evaluations the rates are too thin to act on; nothing is retired.</summary>
    public int MinimumEvaluations { get; init; } = 50;

    public double MaximumOverrideRate { get; init; } = 0.05;

    public double MaximumReversalRate { get; init; } = 0.02;

    public static RetirementBudget Default { get; } = new();
}

/// <summary>A rule's lifecycle state. Suspension is automatic; re-promotion is the full gate again.</summary>
public enum RuleStatus
{
    Live = 0,
    SuspendedPendingReview = 1,
}

/// <summary>The retirement monitor's answer for one rule.</summary>
public sealed record RetirementAssessment
{
    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required RuleStatus Status { get; init; }

    /// <summary>False when the window was too thin to measure; the rule is not silently cleared.</summary>
    public required bool Assessed { get; init; }

    public required int Evaluations { get; init; }

    public required double OverrideRate { get; init; }

    public required double ReversalRate { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Monitors a live rule for override, reversal and false-positive drift and suspends it when its
/// budget is crossed.
/// </summary>
/// <remarks>
/// Suspension is automatic and the rule returns to review rather than being quietly retuned
/// (spec 9.14, step 6). A window below <see cref="RetirementBudget.MinimumEvaluations"/> is reported
/// as not assessed: a rate over four decisions is not evidence, and treating it as a clearance is
/// the same error as scoring a missing dimension as zero.
/// </remarks>
public static class RuleRetirementMonitor
{
    public const string SuspendedForOverrides = "policy.rule_suspended_override_budget";
    public const string SuspendedForReversals = "policy.rule_suspended_reversal_budget";
    public const string NotAssessed = "policy.retirement_not_assessed_insufficient_sample";

    public static RetirementAssessment Assess(
        RuleOutcomeWindow window,
        RetirementBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var limits = budget ?? RetirementBudget.Default;

        if (window.Evaluations < 0
            || window.Overrides < 0
            || window.Reversals < 0
            || window.Overrides > window.Evaluations
            || window.Reversals > window.Evaluations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                "A retirement window cannot hold negative counts, or more overrides or reversals than evaluations.");
        }

        if (window.Evaluations < limits.MinimumEvaluations)
        {
            return new RetirementAssessment
            {
                RuleId = window.RuleId,
                RuleVersion = window.RuleVersion,
                Status = RuleStatus.Live,
                Assessed = false,
                Evaluations = window.Evaluations,
                OverrideRate = Rate(window.Overrides, window.Evaluations),
                ReversalRate = Rate(window.Reversals, window.Evaluations),
                Reasons = [NotAssessed],
            };
        }

        var overrideRate = Rate(window.Overrides, window.Evaluations);
        var reversalRate = Rate(window.Reversals, window.Evaluations);
        var reasons = new List<string>();

        if (overrideRate > limits.MaximumOverrideRate)
        {
            reasons.Add(SuspendedForOverrides);
        }

        if (reversalRate > limits.MaximumReversalRate)
        {
            reasons.Add(SuspendedForReversals);
        }

        return new RetirementAssessment
        {
            RuleId = window.RuleId,
            RuleVersion = window.RuleVersion,
            Status = reasons.Count == 0 ? RuleStatus.Live : RuleStatus.SuspendedPendingReview,
            Assessed = true,
            Evaluations = window.Evaluations,
            OverrideRate = overrideRate,
            ReversalRate = reversalRate,
            Reasons = reasons,
        };
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;
}
