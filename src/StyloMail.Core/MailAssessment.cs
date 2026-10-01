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

    /// <summary>
    /// The configured weight this dimension carried when the assessment was made, or null when the
    /// build that made the decision did not record one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Travelling with the row rather than left to the reader, because an index that cannot be
    /// checked from its own response is not a record (decision 37). The weights are configuration
    /// and configuration moves, so a response that omitted them would read differently after a
    /// settings change while its own numbers stayed fixed.
    /// </para>
    /// <para>
    /// <b>Nullable and still required, which is not a contradiction.</b> Required keeps the
    /// compile-time teeth that stop a projection site dropping the member; nullable is how a row
    /// written before the member existed says so. The two alternatives are both worse: zero would
    /// serve a plausible number on a row that carried weight, and reconstructing the weight from
    /// today's configuration would render a decision that reconciles and reconciles falsely.
    /// </para>
    /// </remarks>
    public required double? Weight { get; init; }

    /// <summary>
    /// Whether this row entered the index's numerator and denominator alike, or null when the build
    /// that made the decision did not record it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>False is not a score of zero.</b> A row that was measured and came back 0.0 is counted
    /// and dilutes the index; a row that was masked contributes nothing at all. The two are
    /// otherwise indistinguishable in a response, which is the defect decision 37 rules on, so the
    /// flag travels with the row.
    /// </para>
    /// <para>
    /// Null is deliberately not derived for a row that predates the flag, even though
    /// <see cref="Availability"/> would suggest it: a counted flag with no weight behind it cannot
    /// reproduce the arithmetic, and publishing half of a pair invites a reader to guess the other
    /// half. A row from before the arithmetic says null here and null on <see cref="Weight"/>.
    /// </para>
    /// </remarks>
    public required bool? Counted { get; init; }

    /// <summary>
    /// Why the row was not counted, when its availability alone does not say it. Null for the
    /// ordinary cases, where "absent" or "unavailable" is the whole explanation.
    /// </summary>
    public string? ExclusionReason { get; init; }
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

    /// <summary>
    /// The channel this decision is about.
    /// </summary>
    /// <remarks>
    /// Required, so a decision can name its own channel rather than leaving a reader to infer it
    /// from the input it came from. A console that has to infer the channel is a console that will
    /// one day show a chat decision and an email decision the same way.
    /// </remarks>
    public required ChannelContext Channel { get; init; }

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

    /// <summary>
    /// The summed weight of the dimensions that were counted: the denominator
    /// <see cref="RiskIndex"/> was divided by. Null when the build that made the decision did not
    /// record it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A documented index is only usable if a reader can check it, and checking it needs the
    /// denominator as well as the rows. With this and the per-dimension weights, a consumer
    /// verifies the published index instead of reconstructing a different one from the same rows,
    /// which is what happened while both were missing (decision 37).
    /// </para>
    /// <para>
    /// <b>Null is not zero, and the zero it would be confused with is real.</b> A denominator of
    /// 0.0 is a measurement: nothing was counted, so there was no index to divide and
    /// <see cref="RiskIndex"/> is 0.0 with it. Null means the decision predates the member. A
    /// reader that merged the two would read an unrecorded arithmetic as an empty one.
    /// </para>
    /// </remarks>
    public required double? RiskIndexDenominator { get; init; }

    /// <summary>
    /// The fraction of the configured dimension weight that was counted: the arithmetic the coverage
    /// floors are compared against, and the number the refusal text already quotes. Null when the
    /// build that made the decision did not record it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floor is only checkable against the quantity it was written for. <see cref="RiskIndexDenominator"/>
    /// is the covered weight, which is this fraction's numerator and never its denominator, so it does
    /// not give a reader the fraction on its own.
    /// </para>
    /// <para>
    /// <b>Nor is it the fraction a reader would sum from the published rows.</b> A dimension that was
    /// never asked (not applicable, or a deterministic signal that produced no evidence) is published
    /// as a masked row carrying its weight, and is deliberately excluded from this denominator. Summing
    /// every published row's weight therefore over-counts the denominator by exactly the never-asked
    /// weight, and yields a percentage smaller than the one the refusal quoted, which reads as a floor
    /// misapplied when the arithmetic was right. That gap is why the fraction is published.
    /// </para>
    /// <para>
    /// <b>Null is not a value, and it is not equal to either end of the range.</b> 1.0 is a measurement
    /// (everything asked was counted) and 0.0 is a measurement (nothing that was asked carried weight);
    /// null means the decision predates the member. A reader that merged null with 0.0 would read an
    /// unrecorded arithmetic as a measured empty one, which is the failure <see cref="RiskIndexDenominator"/>'s
    /// remarks were written to prevent.
    /// </para>
    /// </remarks>
    public required double? CoveredWeightFraction { get; init; }

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
