namespace StyloMail.Core;

/// <summary>How a submission relates to what the queue already held.</summary>
public enum SubmissionAdmission
{
    /// <summary>This request created a new durable submission.</summary>
    Created = 0,

    /// <summary>An existing submission matched on the caller's idempotency key. Not a new resource.</summary>
    Duplicate = 1,
}

/// <summary>One scored dimension of risk, with the evidence that produced it.</summary>
public sealed record RiskDimension
{
    public required string Name { get; init; }

    public required double Score { get; init; }

    public required EvidenceAvailability Availability { get; init; }

    /// <summary>Signal ids that contributed, so a decision can be traced back to its inputs.</summary>
    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>
/// A human-readable justification. Reasons are ordered by significance, most significant first.
/// </summary>
/// <remarks>
/// Reasons must describe the actual evidence, "recipient fan-out rising while payment-redirection
/// evidence also rises", rather than restating a scalar. An unexplained score is not an explanation.
/// </remarks>
public sealed record ReasonCode
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>Signal ids this reason is grounded in.</summary>
    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }
}

/// <summary>Version stamps for everything that shaped a decision. Required for replay and for cache invalidation.</summary>
public sealed record AssessmentVersions
{
    public required string PolicyVersion { get; init; }

    /// <summary>Resolved classifier model id, e.g. <c>jev-1.13.0</c>. Never an alias, aliases move.</summary>
    public string? ClassifierModelVersion { get; init; }

    public required string QuestionSchemaVersion { get; init; }

    public required string PreprocessingVersion { get; init; }

    public IReadOnlyDictionary<string, string>? ProfileVersions { get; init; }

    /// <summary>Behavioural regime marker; changes suppress derivative evidence.</summary>
    public string? RegimeId { get; init; }
}

/// <summary>Provenance of a memoised semantic result, so reuse is always visible in the decision.</summary>
public sealed record CacheProvenance
{
    public required bool Hit { get; init; }

    public required string KeyDigest { get; init; }

    public DateTimeOffset? CachedAt { get; init; }

    public string? ModelVersion { get; init; }

    /// <summary>True when a cached entry was past its expiry but reused for lack of a better answer.</summary>
    public required bool Stale { get; init; }
}

/// <summary>
/// The complete outcome for one assessed message.
/// </summary>
public sealed record MailAssessment
{
    public required string AssessmentId { get; init; }

    public required string InternalMessageId { get; init; }

    public required string TenantId { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required IReadOnlyList<RiskDimension> RiskDimensions { get; init; }

    /// <summary>
    /// Aggregate risk index.
    /// </summary>
    /// <remarks>
    /// <b>A documented index, not a calibrated probability.</b> It is a weighted combination of
    /// correlated semantic dimensions, and correlated outputs must not be multiplied as though
    /// they were independent likelihoods. It is not to be reported as a probability of anything.
    /// </remarks>
    public required double RiskIndex { get; init; }

    public required MailAction Action { get; init; }

    /// <summary>In shadow mode, the action policy would have taken. Forwarding still occurs.</summary>
    public MailAction? ProposedActionInShadow { get; init; }

    /// <summary>
    /// Whether this assessment could have stopped the message or only reacts to it.
    /// </summary>
    /// <remarks>
    /// Required, so it is stated at every construction site rather than defaulted. See
    /// <see cref="DeliveryTiming"/> for why a default is the dangerous option.
    /// </remarks>
    public required DeliveryTiming DeliveryTiming { get; init; }

    /// <summary>
    /// Whether this request created a durable submission or matched an existing one.
    /// </summary>
    /// <remarks>
    /// <b>Null exactly when <see cref="SubmissionId"/> is null</b>, that is, when no durable
    /// submission was created or matched. A caller must read this rather than infer the answer from
    /// the presence of a reason code: reasons are <em>explanations</em>, and digging a fact out of
    /// prose is how a mechanism's phrasing comes to carry a meaning it does not have.
    ///
    /// <para>
    /// This exists because a retry must return the same <see cref="SubmissionId"/> it already has,     /// so the id alone cannot distinguish "I just created this" from "this already existed", and an
    /// HTTP caller needs that fact to choose between 201 and 200.
    /// </para>
    /// </remarks>
    public SubmissionAdmission? Submission { get; init; }

    /// <summary>
    /// The durable queue id, set when the assessor accepted responsibility for delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null when no acceptance occurred</b>, assessment-only traffic, or a decision of
    /// <see cref="MailAction.Defer"/>/<see cref="MailAction.Reject"/> that declined responsibility
    /// before acceptance. A null here is a meaningful "we did not take this", not a missing value.
    /// </para>
    /// <para>
    /// <b>The assessor is the only component that accepts.</b> It runs the full pipeline including
    /// acceptance, and reports the resulting id here; callers such as the HTTP host read it rather
    /// than calling the queue themselves. Two components accepting the same message under different
    /// idempotency keys is how one message becomes two deliveries.
    /// </para>
    /// </remarks>
    public string? SubmissionId { get; init; }

    public required IReadOnlyList<ReasonCode> Reasons { get; init; }

    public required AssessmentVersions Versions { get; init; }

    public required AnalysisCoverage Coverage { get; init; }

    public CacheProvenance? Cache { get; init; }

    public required IReadOnlyList<RecipientDisposition> RecipientDispositions { get; init; }

    public required DateTimeOffset AssessedAt { get; init; }
}
