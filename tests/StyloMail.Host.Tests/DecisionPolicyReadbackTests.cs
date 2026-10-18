using System.Text.Json;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Tests;

/// <summary>
/// The policy decision readback is a projection of the stored record, not a second computation.
/// Null provenance must stay null, and a shadow/fallback record must never read as applied.
/// </summary>
public sealed class DecisionPolicyReadbackTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_pack_record_projects_every_readback_field_unchanged()
    {
        var deadline = Start.AddHours(24);
        var record = Record(PolicySource.Pack) with
        {
            ReviewDeadline = deadline,
            EffectiveInputDigest = new string('b', 64),
        };

        var policy = DecisionResponse.From(Assessment(record)).Policy;

        Assert.NotNull(policy);
        Assert.Equal(PolicySource.Pack, policy!.Source);
        Assert.Equal("pack-commerce", policy.PackId);
        Assert.Equal("1.0.0", policy.PackVersion);
        Assert.Equal("rules-commerce", policy.RuleSetId);
        Assert.Equal("rules/1", policy.RuleSetVersion);
        Assert.Equal("scorer/1", policy.ScorerVersion);
        Assert.Equal("deterministic-policy-evaluator/1", policy.EvaluatorVersion);
        Assert.Equal(PolicyVerdict.Review, policy.Verdict);
        Assert.True(policy.RequiresHumanReview);
        Assert.Equal("rule-payment-redirection", policy.DecidingRuleId);
        Assert.Equal(1, policy.DecidingRuleVersion);
        Assert.Equal(0.35, policy.DecidingThreshold);
        Assert.Equal("policy.payment_redirection_review", policy.DecidingReasonCode);
        Assert.Equal("gray zone", policy.DecidingReason);
        Assert.Equal(1, policy.Coverage);
        Assert.Equal(deadline, policy.ReviewDeadline);
        Assert.Equal(new string('b', 64), policy.EffectiveInputDigest);
        Assert.Equal(MailAction.Allow, policy.LegacyAction);
        Assert.Equal(MailAction.Review, policy.ComposedAction);
        Assert.Equal(MailAction.Review, policy.FinalAction);
        Assert.True(policy.PolicyContributed);
        Assert.True(policy.Applied);
        Assert.True(policy.Durable);
        Assert.Equal(AdmissionOutcome.Admitted, policy.Admission);
    }

    [Fact]
    public void A_fallback_record_keeps_null_provenance_null()
    {
        var record = Record(PolicySource.Fallback) with
        {
            PackId = null,
            PackVersion = null,
            RuleSetId = null,
            RuleSetVersion = null,
            ScorerVersion = null,
            DecidingRuleId = null,
            DecidingRuleVersion = null,
            DecidingThreshold = null,
            EvidenceSnapshotDigest = null,
            EffectiveInput = null,
            EffectiveInputDigest = null,
            FallbackAttempted = true,
            PackUnavailable = true,
            FallbackReason = "policy.pack_store_unavailable",
            Applied = false,
            Durable = false,
            PolicyContributed = false,
            Admission = AdmissionOutcome.NotAttempted,
            LegacyAction = MailAction.Allow,
            ComposedAction = MailAction.Review,
            FinalAction = MailAction.Review,
        };

        var policy = DecisionResponse.From(Assessment(record)).Policy;

        Assert.NotNull(policy);
        Assert.Equal(PolicySource.Fallback, policy!.Source);
        Assert.Null(policy.PackId);
        Assert.Null(policy.PackVersion);
        Assert.Null(policy.RuleSetId);
        Assert.Null(policy.RuleSetVersion);
        Assert.Null(policy.ScorerVersion);
        Assert.Null(policy.DecidingRuleId);
        Assert.Null(policy.DecidingRuleVersion);
        Assert.Null(policy.DecidingThreshold);
        Assert.Null(policy.EvidenceSnapshotDigest);
        Assert.Null(policy.EffectiveInput);
        Assert.Null(policy.EffectiveInputDigest);
        Assert.True(policy.FallbackAttempted);
        Assert.True(policy.PackUnavailable);
        Assert.Equal("policy.pack_store_unavailable", policy.FallbackReason);
        Assert.False(policy.Applied);

        var json = JsonSerializer.Serialize(DecisionResponse.From(Assessment(record)), HostJson.Options);
        Assert.Contains("\"packId\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"evidenceSnapshotDigest\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"applied\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_global_shadow_record_is_never_rendered_as_applied()
    {
        var record = Record(PolicySource.GlobalShadow) with
        {
            Shadow = true,
            Authoritative = true,
            PolicyContributed = true,
            Applied = false,
            LegacyAction = MailAction.Allow,
            ComposedAction = MailAction.Quarantine,
            FinalAction = MailAction.Allow,
            Admission = AdmissionOutcome.Admitted,
            Durable = true,
        };

        var policy = DecisionResponse.From(Assessment(record)).Policy;

        Assert.NotNull(policy);
        Assert.Equal(PolicySource.GlobalShadow, policy!.Source);
        Assert.True(policy.Shadow);
        Assert.True(policy.Authoritative);
        Assert.True(policy.PolicyContributed);
        Assert.False(policy.Applied);
        Assert.Equal(MailAction.Quarantine, policy.ComposedAction);
        Assert.Equal(MailAction.Allow, policy.FinalAction);
    }

    private static PolicyDecisionRecord Record(PolicySource source) => new()
    {
        Source = source,
        PackId = "pack-commerce",
        PackVersion = "1.0.0",
        RuleSetId = "rules-commerce",
        RuleSetVersion = "rules/1",
        ScorerVersion = "scorer/1",
        EvaluatorVersion = "deterministic-policy-evaluator/1",
        Verdict = PolicyVerdict.Review,
        RequiresHumanReview = true,
        DecidingRuleId = "rule-payment-redirection",
        DecidingRuleVersion = 1,
        DecidingThreshold = 0.35,
        DecidingReasonCode = "policy.payment_redirection_review",
        DecidingReason = "gray zone",
        MissingDimensions = [],
        Coverage = 1,
        BindingIds = [],
        PropertyFacts = [],
        MissingPropertyKeys = [],
        EvidenceSnapshotDigest = new string('a', 64),
        EvaluatedAt = Start,
        Admission = AdmissionOutcome.Admitted,
        Applied = true,
        Durable = true,
        LegacyAction = MailAction.Allow,
        ComposedAction = MailAction.Review,
        FinalAction = MailAction.Review,
        PolicyContributed = true,
    };

    private static MailAssessment Assessment(PolicyDecisionRecord policy) => new()
    {
        AssessmentId = "asm-1",
        InternalMessageId = "msg-1",
        TenantId = "acme",
        Channel = ChannelContext.Email,
        Evidence = [],
        RiskDimensions = [],
        RiskIndex = 0,
        RiskIndexDenominator = 0.0,
        CoveredWeightFraction = 1.0,
        Action = MailAction.Review,
        DeliveryTiming = DeliveryTiming.PreAcceptance,
        Reasons = [],
        Versions = new AssessmentVersions
        {
            PolicyVersion = "policy/1",
            QuestionSchemaVersion = "q/1",
            PreprocessingVersion = "p/1",
        },
        Coverage = new AnalysisCoverage
        {
            BodyParsed = true,
            HtmlPresent = false,
            HasAttachments = false,
            HtmlTextDisagreement = false,
            ParserLimitExceeded = false,
            ContentEncrypted = false,
            Truncated = false,
            ConversationContextMissing = false,
        },
        RecipientDispositions = [],
        AssessedAt = Start,
        Policy = policy,
    };
}
