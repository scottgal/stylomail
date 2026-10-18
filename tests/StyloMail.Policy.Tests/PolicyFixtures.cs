using StyloMail.Core;

namespace StyloMail.Policy.Tests;

/// <summary>
/// Shared builders for the pack, rule and evidence fixtures.
/// </summary>
/// <remarks>
/// Every fixture pins the clock and every identifier it uses. A test that reads the system clock is
/// not repeatable, and a test that invents an id inline makes a failure about the fixture rather
/// than about the behaviour under test.
/// </remarks>
internal static class PolicyFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public const string Tenant = "tenant-a";

    public const string PackId = "pack-commerce";

    public const string RuleSetVersion = "rules/1";

    public const string ScorerVersion = "scorer/1";

    public const string PaymentSignal = "semantic.payment_redirection";

    public const string CredentialSignal = "semantic.credential_request";

    public static Dictionary<string, ToleranceLevel> DefaultLevels() => new(StringComparer.Ordinal)
    {
        [PolicyCategories.ChildSafetyGrooming] = ToleranceLevel.Strict,
        [PolicyCategories.CredibleThreatsOfViolence] = ToleranceLevel.Strict,
        [PolicyCategories.IllegalContent] = ToleranceLevel.Strict,
        [PolicyCategories.PaymentRedirection] = ToleranceLevel.Standard,
        [PolicyCategories.CredentialRequest] = ToleranceLevel.Standard,
        [PolicyCategories.Promotion] = ToleranceLevel.Standard,
    };

    public static PolicyPack Pack(
        IReadOnlyDictionary<string, ToleranceLevel>? levels = null,
        PolicyVerdict ceiling = PolicyVerdict.Quarantine,
        PolicyAudience audience = PolicyAudience.Adults,
        TextCaptureRules? capture = null,
        string? secondApprover = null,
        string version = "1.0.0",
        string tenant = Tenant,
        string author = "author-a",
        string approver = "approver-a",
        DateTimeOffset? effectiveFrom = null) => new()
        {
            TenantId = tenant,
            Id = PackId,
            Version = version,
            Context = new PolicyPackContext
            {
                Vertical = PolicyVertical.Commerce,
                Audience = audience,
                Jurisdiction = "GB",
            },
            CategoryLevels = levels ?? DefaultLevels(),
            ActionCeiling = ceiling,
            CaptureText = capture ?? TextCaptureRules.Disabled,
            EvidenceRetentionDays = 30,
            RuleSetVersion = RuleSetVersion,
            ScorerVersion = ScorerVersion,
            Author = author,
            Approver = approver,
            SecondApprover = secondApprover,
            EffectiveFrom = effectiveFrom ?? Now.AddDays(-1),
            ReviewDue = Now.AddMonths(6),
        };

    public static PolicyRule PaymentRule(
        int version = 1,
        IReadOnlyList<RuleBand>? bands = null,
        int minimumSample = 1,
        PolicyVerdict onMissing = PolicyVerdict.Review) => new()
        {
            Id = "rule-payment-redirection",
            Version = version,
            Category = PolicyCategories.PaymentRedirection,
            SignalId = PaymentSignal,
            Comparison = RuleComparison.AtLeast,
            RequiredSignalIds = [PaymentSignal],
            Bands = bands ??
            [
                new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.payment_redirection_quarantine"),
                new RuleBand(0.55, PolicyVerdict.Hold, "policy.payment_redirection_hold"),
                new RuleBand(0.35, PolicyVerdict.Review, "policy.payment_redirection_review"),
            ],
            MinimumSampleSupport = minimumSample,
            OnMissingEvidence = onMissing,
        };

    public static PolicyRule CredentialRule(int version = 1) => new()
    {
        Id = "rule-credential-request",
        Version = version,
        Category = PolicyCategories.CredentialRequest,
        SignalId = CredentialSignal,
        Comparison = RuleComparison.AtLeast,
        RequiredSignalIds = [CredentialSignal],
        Bands =
        [
            new RuleBand(0.80, PolicyVerdict.Quarantine, "policy.credential_request_quarantine"),
            new RuleBand(0.55, PolicyVerdict.Hold, "policy.credential_request_hold"),
        ],
        MinimumSampleSupport = 1,
    };

    public static PolicyRule GroomingRule(int version = 1) => new()
    {
        Id = "rule-grooming",
        Version = version,
        Category = PolicyCategories.ChildSafetyGrooming,
        SignalId = "semantic.grooming",
        Comparison = RuleComparison.AtLeast,
        RequiredSignalIds = ["semantic.grooming"],
        Bands =
        [
            // Deliberately Observe: zero tolerance must elevate it, not the band.
            new RuleBand(0.50, PolicyVerdict.Observe, "policy.grooming_observed"),
        ],
        MinimumSampleSupport = 1,
    };

    public static PolicyRuleSet RuleSet(params PolicyRule[] rules) => new()
    {
        Id = "rules-commerce",
        Version = RuleSetVersion,
        Rules = rules.Length == 0 ? [PaymentRule()] : rules,
    };

    public static Evidence Signal(
        string signalId,
        double value,
        int? sampleSupport = 10,
        string sourceVersion = ScorerVersion,
        EvidenceAvailability availability = EvidenceAvailability.Available) => new()
        {
            SignalId = signalId,
            Origin = EvidenceOrigin.Semantic,
            Availability = availability,
            Value = availability == EvidenceAvailability.Available ? value : null,
            Confidence = null,
            SampleSupport = sampleSupport,
            SourceVersion = sourceVersion,
            ObservedAt = Now.AddMinutes(-1),
            ObservedScope = "message",
        };

    public static Evidence Unavailable(string signalId) => new()
    {
        SignalId = signalId,
        Origin = EvidenceOrigin.Semantic,
        Availability = EvidenceAvailability.Unavailable,
        Value = null,
        Confidence = null,
        SampleSupport = null,
        SourceVersion = ScorerVersion,
        ObservedAt = Now.AddMinutes(-1),
    };

    public static PolicyEvaluationInput Input(
        PolicyRuleSet? ruleSet = null,
        IReadOnlyList<Evidence>? evidence = null,
        PolicyPack? pack = null,
        string tenant = Tenant) => new()
        {
            TenantId = tenant,
            Pack = pack ?? Pack(),
            RuleSet = ruleSet ?? RuleSet(),
            Evidence = evidence ?? [Signal(PaymentSignal, 0.9)],
        };

    public static PolicyEvaluation Evaluate(PolicyEvaluationInput input) =>
        new DeterministicPolicyEvaluator(new FixedTimeProvider()).Evaluate(input);

    public static PolicyBinding Binding(
        BindingAction action = BindingAction.Permit,
        string? categoryId = null,
        ToleranceLevel? targetLevel = null,
        string? approvedBy = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? reviewBy = null) => new()
        {
            Id = "binding-1",
            TenantId = Tenant,
            ScopeKind = BindingScope.Entity,
            ScopeValue = "entity-1",
            Action = action,
            CategoryId = categoryId,
            TargetLevel = targetLevel,
            PackVersion = "1.0.0",
            Author = "operator-a",
            ApprovedBy = approvedBy,
            Reason = "The tenant is a school; this supplier is its known payment processor.",
            StartsAt = startsAt ?? Now.AddDays(-1),
            ExpiresAt = expiresAt ?? Now.AddDays(30),
            ReviewBy = reviewBy ?? Now.AddDays(20),
            OnExpiry = BindingOnExpiry.HoldForReview,
        };

    public static TextCaptureRules ExcerptCapture() => new()
    {
        Mode = TextCaptureMode.Excerpt,
        MaxCharacters = 500,
        RetentionDays = 7,
        Statement = "A 500-character excerpt is kept for 7 days for review only.",
    };

    public static CandidateRule Candidate(
        LabelWindow? training = null,
        LabelWindow? heldOut = null,
        PolicyRule? rule = null,
        string sourcePackId = PackId) => new()
        {
            Rule = rule ?? PaymentRule(version: 2),
            SourcePackId = sourcePackId,
            Author = "author-a",
            TrainingWindow = training ?? new LabelWindow { From = Now.AddDays(-90), To = Now.AddDays(-30) },
            HeldOutWindow = heldOut ?? new LabelWindow { From = Now.AddDays(-30), To = Now },
        };

    public static PromotionEvidence PromotionEvidence(
        string targetPackId = PackId,
        int samples = 100,
        int humanConfirmed = 60,
        int modelLabeled = 40,
        double precision = 0.95,
        double recall = 0.8,
        double falsePositiveRate = 0.05,
        int shadowSamples = 40,
        double shadowAgreement = 0.95,
        bool crossPackValidated = false,
        int crossPackValidationSamples = 0) => new()
        {
            TargetPackId = targetPackId,
            SampleCount = samples,
            HumanConfirmedCount = humanConfirmed,
            ModelLabeledCount = modelLabeled,
            Precision = precision,
            Recall = recall,
            FalsePositiveRate = falsePositiveRate,
            ShadowSamples = shadowSamples,
            ShadowAgreement = shadowAgreement,
            ModelVersions = ["llm-decision/2026-10"],
            CrossPackValidated = crossPackValidated,
            CrossPackValidationSamples = crossPackValidationSamples,
        };

    public sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
