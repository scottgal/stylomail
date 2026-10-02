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

    /// <summary>Where this message came from. Email is the only channel we can refuse before delivery.</summary>
    public required ChannelContext Channel { get; init; }

    /// <summary>
    /// Whether anything could have been done, or whether it was already gone.
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted, matching the assessment. Defaulting it
    /// would silently assert <see cref="DeliveryTiming.PreAcceptance"/> on a
    /// decision that says nothing of the kind, which is the most consequential
    /// possible default on this type.
    /// </remarks>
    public required DeliveryTiming DeliveryTiming { get; init; }

    /// <summary>In shadow mode, the action policy would have taken. Forwarding still occurred.</summary>
    public MailAction? ProposedActionInShadow { get; init; }

    /// <summary>
    /// An aggregate risk index. A documented index, <b>not</b> a calibrated
    /// probability, and the pane must not present it as a confidence percentage.
    /// </summary>
    public required double RiskIndex { get; init; }

    /// <summary>
    /// The summed weight of the counted dimensions: the denominator
    /// <see cref="RiskIndex"/> was divided by.
    /// </summary>
    /// <remarks>
    /// <b>Null is not zero.</b> A served <c>0</c> is a measured empty
    /// arithmetic, the decision whose every dimension went uncounted; a served
    /// <c>null</c> is a decision made before the arithmetic was recorded, whose
    /// weight is not recoverable from the rows. The pane says which of the two
    /// it is looking at, because an index that cannot be checked from its own
    /// response is not a record.
    /// </remarks>
    public required double? RiskIndexDenominator { get; init; }

    /// <summary>
    /// The share of the asked weight that carried weight, as the refusal text
    /// quotes it. <see cref="RiskIndexDenominator"/> is this fraction's
    /// NUMERATOR and never its denominator, so a pane holding only that field
    /// cannot recover the ratio.
    /// </summary>
    /// <remarks>
    /// <b>Null is neither zero nor one.</b> Both ends of the range are
    /// <i>measurements</i>: a served <c>1</c> is every asked dimension counted,
    /// and a served <c>0</c> is none of what was asked carrying weight. A served
    /// <c>null</c> is a decision made by a build that did not record the member,
    /// which is a row written before it existed, so the pane says the fraction
    /// is not recorded rather than rendering an unrecorded arithmetic as a
    /// measured empty one. The Host serves an explicit <c>null</c> for those
    /// rows rather than omitting the member.
    /// </remarks>
    public required double? CoveredWeightFraction { get; init; }

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

    /// <summary>
    /// This row's weight in the index, or null when the decision predates the
    /// arithmetic being served.
    /// </summary>
    /// <remarks>
    /// Required as well as nullable, deliberately: an optional member can be
    /// dropped by a future projection without a compile error, and a row that
    /// dropped it would stop being checkable while still looking complete.
    /// </remarks>
    public required double? Weight { get; init; }

    /// <summary>
    /// Whether this row entered the index's numerator and denominator alike, or
    /// null when the decision predates the flag.
    /// </summary>
    /// <remarks>
    /// <b>False is not a score of zero.</b> A row that was measured and came
    /// back <c>0.0</c> was counted and dilutes the index; a row decision 31
    /// masked contributed nothing at all. Both arrive as <c>score: 0,
    /// availability: Available</c>, so the flag travels with the row rather than
    /// being inferred from the score, and null is a third state that is not
    /// derived for an older row.
    /// </remarks>
    public required bool? Counted { get; init; }

    /// <summary>
    /// Why this row was not counted, when its availability alone does not say
    /// it. Null on a counted row and on the ordinary unavailable case.
    /// </summary>
    public string? ExclusionReason { get; init; }

    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>One piece of evidence.</summary>
/// <remarks>
/// There is no <c>Attributes</c> member because the Host deliberately does not
/// project one: it is the field most likely to carry content-derived detail,
/// and this view is served to any principal holding the review privilege.
/// <see cref="Window"/> is the one named exception, projected on its own
/// because it is structural rather than content-derived and because two trend
/// rows cannot be told apart without it.
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

    /// <summary>
    /// Which of the producer's trend windows this row came from, or null when the signal is not
    /// windowed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third part of a signal's identity here. The behavioural evaluator emits one row per trend
    /// window, so two rows can agree on <see cref="SignalId"/> and <see cref="ObservedScope"/> and
    /// differ only here: the producer's two windows are "burst" and "slow". Keying on the pair alone
    /// collapses two facts into one.
    /// </para>
    /// <para>
    /// <b>Null is a fact, not a gap.</b> Most rows are not windowed at all: every semantic row, every
    /// drift row. The pane renders it as absent rather than as an unknown a retry might resolve, and
    /// it is deliberately not to be confused with <see cref="Availability"/>, which is a separate
    /// required field answering whether the value could be produced at all. A row can be
    /// <c>NotApplicable</c> with a perfectly good window, and windowed with no value.
    /// </para>
    /// </remarks>
    public string? Window { get; init; }

    /// <summary>
    /// Why this row reads the way it does, in the producer's own words. Empty when the row needs no
    /// explanation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only channel that carries a cut or a refusal, and the console must render it.</b>
    /// The Host's own remark for the coverage block says the rule outright: a distinction the wire
    /// declines to carry is carried by the reason code instead. So the shortening of a body, and the
    /// refusal to answer at all, reach an operator HERE and nowhere else. <see cref="CoverageResponse.Truncated"/>
    /// is a different fact (parser coverage, not the fit), and the fit's kept length is request-side
    /// only and never appears on this wire at all.
    /// </para>
    /// <para>
    /// Mirrored rather than referenced, like every type in this file. A list because the wire's field
    /// is one, and because a row can be explained by more than one condition.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AvailabilityReasons { get; init; } = [];
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
