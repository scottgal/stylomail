using StyloMail.Core;

namespace StyloMail.Host.Contracts;

/// <summary>
/// The explainable decision view, returned by both <c>POST /v1/assessments</c> and
/// <c>GET /v1/decisions/{id}</c>.
/// </summary>
/// <remarks>
/// One projection for both routes on purpose: a caller that just assessed a message and a
/// reviewer reading the ledger should be looking at the same explanation, not two renderings
/// that can drift apart.
///
/// <para>
/// Recipient dispositions are included but are scoped: nothing here exposes one recipient's
/// relationship history or address to another recipient.
/// </para>
/// </remarks>
public sealed record DecisionResponse
{
    public required string AssessmentId { get; init; }

    public required string InternalMessageId { get; init; }

    public required MailAction Action { get; init; }

    /// <summary>In shadow mode, the action policy would have taken. Forwarding still occurred.</summary>
    public MailAction? ProposedActionInShadow { get; init; }

    /// <summary>
    /// The aggregate risk index. A documented index, <b>not</b> a calibrated probability, and not
    /// to be read as one.
    /// </summary>
    public required double RiskIndex { get; init; }

    public required IReadOnlyList<ReasonResponse> Reasons { get; init; }

    public required IReadOnlyList<RiskDimensionResponse> RiskDimensions { get; init; }

    public required IReadOnlyList<EvidenceResponse> Evidence { get; init; }

    public required VersionsResponse Versions { get; init; }

    public required CoverageResponse Coverage { get; init; }

    public CacheResponse? Cache { get; init; }

    public required IReadOnlyList<RecipientResponse> Recipients { get; init; }

    public required DateTimeOffset AssessedAt { get; init; }

    public static DecisionResponse From(MailAssessment assessment) => new()
    {
        AssessmentId = assessment.AssessmentId,
        InternalMessageId = assessment.InternalMessageId,
        Action = assessment.Action,
        ProposedActionInShadow = assessment.ProposedActionInShadow,
        RiskIndex = assessment.RiskIndex,
        Reasons = [.. assessment.Reasons.Select(ReasonResponse.From)],
        RiskDimensions = [.. assessment.RiskDimensions.Select(d => new RiskDimensionResponse
        {
            Name = d.Name,
            Score = d.Score,
            Availability = d.Availability,
            EvidenceSignalIds = d.EvidenceSignalIds,
        })],
        Evidence = [.. assessment.Evidence.Select(e => new EvidenceResponse
        {
            SignalId = e.SignalId,
            Origin = e.Origin,
            Availability = e.Availability,
            Value = e.Value,
            Confidence = e.Confidence,
            SampleSupport = e.SampleSupport,
            SourceVersion = e.SourceVersion,
            ObservedAt = e.ObservedAt,
            ObservedScope = e.ObservedScope,
        })],
        Versions = VersionsResponse.From(assessment.Versions),
        Coverage = CoverageResponse.From(assessment.Coverage),
        Cache = assessment.Cache is null
            ? null
            : new CacheResponse
            {
                Hit = assessment.Cache.Hit,
                KeyDigest = assessment.Cache.KeyDigest,
                CachedAt = assessment.Cache.CachedAt,
                ModelVersion = assessment.Cache.ModelVersion,
                Stale = assessment.Cache.Stale,
            },
        Recipients = [.. assessment.RecipientDispositions.Select(d => new RecipientResponse
        {
            Recipient = d.Recipient,
            RecipientRisk = d.RecipientRisk,
            Action = d.Action,
            DeliveryState = d.DeliveryState,
            ReEvaluateBy = d.ReEvaluateBy,
        })],
        AssessedAt = assessment.AssessedAt,
    };
}

public sealed record ReasonResponse
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }

    /// <summary>
    /// The one mapping from Core's reason type to this one.
    /// </summary>
    /// <remarks>
    /// Shared by the detail view and the ledger listing deliberately. Two projections of the same
    /// part of a decision are two places for the explanation to drift, and an explanation that
    /// differs between the list and the detail is worse than one that is missing.
    /// </remarks>
    public static ReasonResponse From(ReasonCode reason) => new()
    {
        Code = reason.Code,
        Message = reason.Message,
        EvidenceSignalIds = reason.EvidenceSignalIds,
    };
}

public sealed record RiskDimensionResponse
{
    public required string Name { get; init; }

    public required double Score { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>
/// One piece of evidence. <c>Attributes</c> is deliberately not projected: it is the field most
/// likely to accumulate content-derived detail, and this view is served to any principal holding
/// the review privilege.
/// </summary>
public sealed record EvidenceResponse
{
    public required string SignalId { get; init; }

    public required EvidenceOrigin Origin { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    public double? Value { get; init; }

    public double? Confidence { get; init; }

    public int? SampleSupport { get; init; }

    public required string SourceVersion { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public string? ObservedScope { get; init; }
}

public sealed record VersionsResponse
{
    public required string PolicyVersion { get; init; }

    public string? ClassifierModelVersion { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    public required string PreprocessingVersion { get; init; }

    public string? RegimeId { get; init; }

    public static VersionsResponse From(AssessmentVersions versions) => new()
    {
        PolicyVersion = versions.PolicyVersion,
        ClassifierModelVersion = versions.ClassifierModelVersion,
        QuestionSchemaVersion = versions.QuestionSchemaVersion,
        PreprocessingVersion = versions.PreprocessingVersion,
        RegimeId = versions.RegimeId,
    };
}

public sealed record CoverageResponse
{
    public required bool BodyParsed { get; init; }

    public required bool HtmlPresent { get; init; }

    public required bool HasAttachments { get; init; }

    public required bool HtmlTextDisagreement { get; init; }

    public required bool ParserLimitExceeded { get; init; }

    public required bool ContentEncrypted { get; init; }

    public required bool Truncated { get; init; }

    public required bool ConversationContextMissing { get; init; }

    /// <summary>
    /// The coverage flags this view carries.
    /// </summary>
    /// <remarks>
    /// <c>OversizeRejected</c> is deliberately not projected, matching the detail view: a separate
    /// field for it would widen a response served to any principal holding Review, and the
    /// distinction it draws is already available to an operator through the reason codes. If a
    /// reviewer surface needs it, add it to both projections at once rather than to one.
    /// </remarks>
    public static CoverageResponse From(AnalysisCoverage coverage) => new()
    {
        BodyParsed = coverage.BodyParsed,
        HtmlPresent = coverage.HtmlPresent,
        HasAttachments = coverage.HasAttachments,
        HtmlTextDisagreement = coverage.HtmlTextDisagreement,
        ParserLimitExceeded = coverage.ParserLimitExceeded,
        ContentEncrypted = coverage.ContentEncrypted,
        Truncated = coverage.Truncated,
        ConversationContextMissing = coverage.ConversationContextMissing,
    };
}

public sealed record CacheResponse
{
    public required bool Hit { get; init; }

    public required string KeyDigest { get; init; }

    public DateTimeOffset? CachedAt { get; init; }

    public string? ModelVersion { get; init; }

    public required bool Stale { get; init; }
}

/// <summary>One recipient's disposition. Scoped to that recipient; never another's history.</summary>
public sealed record RecipientResponse
{
    public required string Recipient { get; init; }

    public required double RecipientRisk { get; init; }

    public required MailAction Action { get; init; }

    public required DeliveryState DeliveryState { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }
}
