namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// The explainable decision view, as returned by <c>POST /v1/assessments</c> and
/// <c>GET /v1/decisions/{id}</c>. Mirrors <c>StyloMail.Host.Contracts.DecisionResponse</c>.
/// </summary>
/// <remarks>
/// The shape is the console's whole reason for existing: it answers "why was
/// this held" with evidence and ordered reason codes rather than a score. The
/// fields below are therefore rendered as they are, not summarised.
///
/// <para>
/// <see cref="RequiredAttribute"/>-style <c>required</c> members are used in
/// exactly the places the Host marks them required. The consequence is that a
/// response missing one fails to bind and surfaces as
/// <see cref="StyloMailApiFailure.UnreadableResponse"/> rather than silently
/// arriving with a default value: a decision pane quietly showing
/// <c>Allow</c> for a field the Host stopped sending would be worse than an
/// error.
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
    /// An aggregate risk index. A documented index, <b>not</b> a calibrated
    /// probability, and the pane must not present it as a confidence percentage.
    /// </summary>
    public required double RiskIndex { get; init; }

    /// <summary>Ordered reason codes: the answer to "why", before any score is shown.</summary>
    public required IReadOnlyList<ReasonResponse> Reasons { get; init; }

    public required IReadOnlyList<RiskDimensionResponse> RiskDimensions { get; init; }

    public required IReadOnlyList<EvidenceResponse> Evidence { get; init; }

    public required VersionsResponse Versions { get; init; }

    public required CoverageResponse Coverage { get; init; }

    public CacheResponse? Cache { get; init; }

    public required IReadOnlyList<RecipientResponse> Recipients { get; init; }

    public required DateTimeOffset AssessedAt { get; init; }
}

/// <summary>One ordered reason. Carries the ids of the evidence that produced it.</summary>
public sealed record ReasonResponse
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The signals behind this reason.
    /// </summary>
    /// <remarks>
    /// This is what makes the pane navigable rather than merely informative: a
    /// reason with no visible evidence is an assertion, and the operator needs
    /// to be able to follow it to the observation that produced it.
    /// </remarks>
    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>One scored dimension of risk, with the availability that qualifies its score.</summary>
public sealed record RiskDimensionResponse
{
    public required string Name { get; init; }

    public required double Score { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>One piece of evidence.</summary>
/// <remarks>
/// There is no <c>Attributes</c> member because the Host deliberately does not
/// project one: it is the field most likely to carry content-derived detail,
/// and this view is served to any principal holding the review privilege.
/// </remarks>
public sealed record EvidenceResponse
{
    public required string SignalId { get; init; }

    public required EvidenceOrigin Origin { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    public double? Value { get; init; }

    /// <summary>
    /// Null for semantic signals, and expected to be.
    /// </summary>
    /// <remarks>
    /// The verified Jev contract returns a Noul answer with a probability and
    /// <b>no confidence field at all</b>, measured against the live API on
    /// 2026-09-22. A null here is the documented shape, not missing data, and
    /// the pane must not render it as an unknown that a retry might resolve.
    /// </remarks>
    public double? Confidence { get; init; }

    public int? SampleSupport { get; init; }

    public required string SourceVersion { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public string? ObservedScope { get; init; }
}

/// <summary>Which versions produced this decision. The ledger entry is meaningless without them.</summary>
public sealed record VersionsResponse
{
    public required string PolicyVersion { get; init; }

    /// <summary>Null when the semantic path was unavailable for this message.</summary>
    public string? ClassifierModelVersion { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    public required string PreprocessingVersion { get; init; }

    public string? RegimeId { get; init; }
}

/// <summary>What the analysis actually saw. The qualifier on every number above it.</summary>
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
}

/// <summary>Whether this decision reused an earlier semantic assessment.</summary>
public sealed record CacheResponse
{
    public required bool Hit { get; init; }

    public required string KeyDigest { get; init; }

    public DateTimeOffset? CachedAt { get; init; }

    public string? ModelVersion { get; init; }

    /// <summary>True when the pinned model id has moved since this was cached.</summary>
    public required bool Stale { get; init; }
}

/// <summary>One recipient's disposition. Scoped to that recipient; never another's history.</summary>
public sealed record RecipientResponse
{
    public required string Recipient { get; init; }

    /// <summary>Recipient-scoped risk, distinct from the message-level index.</summary>
    public required double RecipientRisk { get; init; }

    public required MailAction Action { get; init; }

    public required DeliveryState DeliveryState { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }
}
