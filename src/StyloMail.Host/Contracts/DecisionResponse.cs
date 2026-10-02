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
    /// Which channel the decision is about.
    /// </summary>
    /// <remarks>
    /// Sent as the context rather than as a bare kind, because the workspace, the channel and the
    /// thread are what make a chat decision locatable by the operator who has to review it, and a
    /// reader that has to go and find the input to learn them is a reader the ledger has failed.
    /// </remarks>
    public required ChannelContext Channel { get; init; }

    /// <summary>
    /// Whether this assessment could have stopped the message or only reacts to it.
    /// </summary>
    /// <remarks>
    /// Present on every decision, chat or email, because the interface promises the console shows it
    /// so an operator never reads a post-hoc hold as a prevention. A decision that omits it leaves
    /// the reader to infer whether the system could have acted, which is the inference this
    /// property exists to prevent.
    /// </remarks>
    public required DeliveryTiming DeliveryTiming { get; init; }

    /// <summary>
    /// The aggregate risk index. A documented index, <b>not</b> a calibrated probability, and not
    /// to be read as one.
    /// </summary>
    public required double RiskIndex { get; init; }

    /// <summary>
    /// The summed weight of the counted dimensions: the denominator <see cref="RiskIndex"/> was
    /// divided by.
    /// </summary>
    /// <remarks>
    /// Served beside the index rather than left to the reader, because an index that cannot be
    /// checked from its own response is not a record (decision 37). The denominator cannot be
    /// recovered from the rows: it counts only what entered it, and the rows that did not are exactly
    /// the ones a reader would have to guess about. Without this, a caller recomputing
    /// <c>sum(weight * score) / sum(weight)</c> from the served rows adds the masked row's weight back
    /// in and gets the pre-decision-31 index, a wrong number that agrees with a shape the system no
    /// longer has.
    /// <para>
    /// <b>Null is not zero.</b> A served <c>0</c> is the measured empty arithmetic, the decision whose
    /// every dimension went uncounted; a served <c>null</c> is a decision made before the arithmetic
    /// was recorded at all, and its weight is not recoverable from the row. Merging them would read an
    /// unrecorded index as an empty one.
    /// </para>
    /// </remarks>
    public required double? RiskIndexDenominator { get; init; }

    /// <summary>
    /// The fraction of configured dimension weight the coverage floors were compared against, and the
    /// number the refusal text quotes as a percentage. Null when the decision's build did not record it.
    /// </summary>
    /// <remarks>
    /// Served for the same reason as the denominator above: a coverage floor produces the served reason
    /// text, so without this a caller can read the quoted percentage and check nothing against it.
    /// <b>It is not the fraction a caller sums from the served rows.</b> A never-asked dimension is
    /// served carrying its weight and is excluded from this fraction's denominator, so summing every
    /// served row's weight over-counts that denominator and yields a smaller percentage.
    /// </remarks>
    public required double? CoveredWeightFraction { get; init; }

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
        Channel = assessment.Channel,
        DeliveryTiming = assessment.DeliveryTiming,
        RiskIndex = assessment.RiskIndex,
        RiskIndexDenominator = assessment.RiskIndexDenominator,
        CoveredWeightFraction = assessment.CoveredWeightFraction,
        Reasons = [.. assessment.Reasons.Select(ReasonResponse.From)],
        RiskDimensions = [.. assessment.RiskDimensions.Select(d => new RiskDimensionResponse
        {
            Name = d.Name,
            Score = d.Score,
            Availability = d.Availability,
            Weight = d.Weight,
            Counted = d.Counted,
            ExclusionReason = d.ExclusionReason,
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
            Window = WindowOf(e),
            AvailabilityReasons = AvailabilityReasonsOf(e),
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

    /// <summary>
    /// The attribute name the behavioural producer writes a trend's window under.
    /// </summary>
    /// <remarks>
    /// Written out here rather than shared with the producer, which publishes it as an attribute name
    /// on <c>BehaviouralEvidence.Trend</c> rather than as a contract. The cost of the two drifting is
    /// that the console shows null where a window belongs, which is why the listing test asserts the
    /// window's actual value and not merely that the field exists.
    /// </remarks>
    private const string WindowAttribute = "window";

    /// <summary>
    /// Reads the trend window off a piece of evidence, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// First match wins, which is safe for this name and not a pattern to copy. The producer writes
    /// one window per row, whereas <c>Attributes</c> is a list rather than a map precisely because
    /// other names are genuinely multi-valued: several reduced-coverage reasons, several masked
    /// dimension ids. A reader that wants one of those must not stop at the first, and must not
    /// mistake this helper for the general case.
    /// </remarks>
    private static string? WindowOf(Evidence evidence)
        => (evidence.Attributes ?? [])
            .FirstOrDefault(attribute => string.Equals(attribute.Name, WindowAttribute, StringComparison.Ordinal))
            ?.Value;

    private const string ReasonAttribute = "reason";

    /// <summary>
    /// EVERY reason a row carries for its availability, in order, or empty when it carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list, and deliberately not the single nullable value <see cref="Window"/> uses, because
    /// <see cref="WindowAttribute"/>'s own remark names this case as the one a first-match reader
    /// must not be copied into: it says other names are "genuinely multi-valued", and gives "several
    /// reduced-coverage reasons" as the instance. A reader that took the first would silently drop
    /// the rest, which for a refusal reason is the difference between naming the cause and naming
    /// one of its causes.
    /// </para>
    /// <para>
    /// Read by name rather than by widening the projection to <c>Attributes</c>, on the same terms
    /// the sibling members use. Empty values are dropped rather than published as empty strings,
    /// because a producer that writes the attribute with no value has said nothing.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> AvailabilityReasonsOf(Evidence evidence)
        => [.. (evidence.Attributes ?? [])
            .Where(attribute => string.Equals(attribute.Name, ReasonAttribute, StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .Where(value => !string.IsNullOrEmpty(value))];
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

    /// <summary>
    /// The configured weight this row carried when the assessment was made, or null when the decision
    /// was made before the arithmetic was recorded.
    /// </summary>
    /// <remarks>
    /// Required, not optional, deliberately: an optional member can be dropped by a future projection
    /// site without a compile error, and a response that drops it stops being checkable while still
    /// looking complete. The weights are configuration and configuration moves, so a response that
    /// omitted them would read differently after a settings change while its own numbers stayed
    /// fixed. Nullable as well as required is what lets a pre-37 decision say "not recorded" without
    /// serving a weight of zero, which would be a plausible number rather than an admission.
    /// </remarks>
    public required double? Weight { get; init; }

    /// <summary>
    /// Whether this row entered the index's numerator and denominator alike, or null when the decision
    /// was made before the flag was recorded.
    /// </summary>
    /// <remarks>
    /// <b>False is not a score of zero.</b> A row that was measured and came back <c>0.0</c> was
    /// counted and dilutes the index; a row decision 31 masked contributed nothing at all. Both serve
    /// as <c>score: 0, availability: Available</c>, which is the defect decision 37 rules on, so the
    /// flag travels with the row rather than being inferred from the score.
    /// <para>
    /// Null is a third state and is not derived for a pre-37 row, even though its availability would
    /// suggest a value: a counted flag with no weight behind it cannot reproduce the arithmetic, and
    /// serving half of a pair invites a reader to guess the other half. A client renders null as
    /// "unrecorded" rather than as false.
    /// </para>
    /// </remarks>
    public required bool? Counted { get; init; }

    /// <summary>
    /// Why this row was not counted, when its availability alone does not say it.
    /// </summary>
    /// <remarks>
    /// Null for the ordinary cases, where "absent" or "unavailable" is the whole explanation, and
    /// null on a counted row. Set where a row is available and still excluded, which today means
    /// decision 31's one-sided mask. A client renders nothing for null rather than an empty reason.
    /// </remarks>
    public string? ExclusionReason { get; init; }

    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>
/// One piece of evidence. <c>Attributes</c> is deliberately not projected wholesale: it is the field
/// most likely to accumulate content-derived detail, and this view is served to any principal holding
/// the review privilege. <see cref="Window"/> is the one named exception, given its own field because
/// it is structural rather than content-derived and because a client cannot tell two trend rows apart
/// without it.
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

    /// <summary>
    /// Which of the producer's trend windows this row came from, or null when the signal is not
    /// windowed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is a fact, not a gap.</b> A signal id is not unique within one assessment: the
    /// behavioural evaluator emits a velocity and an acceleration for each of its trend windows, so
    /// two rows can agree on <see cref="SignalId"/> and <see cref="ObservedScope"/> and differ only
    /// here. A client keying on that pair alone collapses them into one, which is what forced the
    /// console to display duplicate-looking rows it could not explain. Null means the producer does
    /// not partition this signal by window: a semantic row, or a drift row. It never means the value
    /// was lost.
    /// </para>
    /// <para>
    /// <b>Not to be confused with <see cref="Availability"/>.</b> Whether evidence could be computed
    /// at all is that field's job, and it is always present. This one answers the narrower question
    /// of which slice of a series the row describes, and it is legitimately null across whole
    /// families of evidence that were computed perfectly well. A client that renders null here as
    /// "unknown" will misreport every one of them.
    /// </para>
    /// <para>
    /// Read by name rather than by widening the projection to <c>Attributes</c>, on the same terms
    /// the record summary gives: this is a window name from the producer's own options, and a later
    /// addition to that list must not reach a caller by default.
    /// </para>
    /// </remarks>
    public string? Window { get; init; }

    /// <summary>
    /// Why this row has the availability it has, when its producer recorded a reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty for a row that carries none, which is not the same as a row that is available: the
    /// twelve semantic rows on a refusal carried no reason here until the adapter began writing one,
    /// and the producers that already did are the behavioural and campaign origins.
    /// </para>
    /// <para>
    /// <b>Servable, unlike the raw <c>Attributes</c>.</b> The evidence projection is read by name so
    /// that a later addition to the attribute list cannot reach a caller by default; this member is
    /// therefore the deliberate exception for this name, and adding it is what makes a refusal's
    /// cause visible to the console rather than only to whatever reads the ledger.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AvailabilityReasons { get; init; } = [];
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

    /// <summary>
    /// The digest of the state this assessment was computed from, or the reason it has none.
    /// </summary>
    /// <remarks>
    /// Not the store's lookup key, which is a different property that happens to share this name: that one
    /// is computed inside the semantic cache from the canonicalised input, while this field is the
    /// <c>CacheProvenance</c> digest projected unchanged from Core. Two properties under one name is the
    /// conflation that cost a peer an over-call, so the distinction is written here rather than inferred.
    /// </remarks>
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
