namespace StyloMail.Core;

/// <summary>Per-request context that must not be smuggled in through message content.</summary>
public sealed record AssessmentContext
{
    public required string TenantId { get; init; }

    /// <summary>
    /// Shadow mode: assess and record the proposed action, but forward regardless.
    /// Shadow is a property of the request, not a kind of action.
    /// </summary>
    public required bool ShadowMode { get; init; }

    /// <summary>
    /// True for assessment-only calls, which must not send mail, advance delivery state,
    /// or feed live traffic accounting. Callers wanting assessment without participating
    /// in live accounting use the assessment path, not submission.
    /// </summary>
    public required bool AssessmentOnly { get; init; }

    /// <summary>Correlation id for our own audit ledger. The provider supplies none.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The caller's idempotency key for a submission, when the caller supplied one.
    /// </summary>
    /// <remarks>
    /// <b>This, and never an internally minted id, is the queue's idempotency key.</b> A client that
    /// retries a submission sends the same key and must receive the same queue id back (§12). An
    /// internally generated value, an assessment id, for instance, is new on every attempt, so
    /// using it as the key makes every retry a fresh queue entry and defeats replay entirely.
    ///
    /// <para>
    /// Null for assessment-only traffic and for callers that do not participate in replay.
    /// </para>
    /// </remarks>
    public string? ClientIdempotencyKey { get; init; }

    /// <summary>
    /// Injected clock. Required so replay can run against isolated state with fixed time
    /// rather than wall-clock, which is what makes decisions reproducible.
    /// </summary>
    public required TimeProvider TimeProvider { get; init; }
}

/// <summary>Input to the semantic classifier. A bounded, structured view of the message.</summary>
public sealed record SemanticMailInput
{
    public required MailAnalysisInput Message { get; init; }

    public required IReadOnlyList<SemanticDimension> Dimensions { get; init; }

    /// <summary>Explicitly tagged contextual facts the classifier may rely on. Kept separate from message content.</summary>
    public IReadOnlyDictionary<string, string>? TaggedContext { get; init; }

    /// <summary>
    /// How this sender has been behaving, so the classifier is not judging the message in isolation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The profile is part of the classifier input, and therefore part of the semantic cache
    /// key.</b> Two messages with identical content and different sender behaviour must not share a
    /// cached assessment, and a cache key that digested only the message would let them.
    /// </para>
    /// <para>
    /// Null means no profile was available at all. That is distinct from a profile carrying
    /// <c>ProfileAvailable: false</c>, which is a positive statement that we looked and found
    /// nothing. Either way the assessment must record that it was made without behavioural context,
    /// so a reader can tell an informed judgement from an uninformed one.
    /// </para>
    /// </remarks>
    public BehaviouralProfile? Profile { get; init; }
}

/// <summary>
/// Result of semantic classification: one piece of evidence per asked dimension, plus the
/// provenance needed to memoise and audit it.
/// </summary>
public sealed record SemanticAssessment
{
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>
    /// Resolved model id that answered, e.g. <c>jev-1.13.0</c>. Reported by the provider and
    /// recorded because an alias like <c>jev-latest</c> moves without notice.
    /// </summary>
    public string? ResolvedModelVersion { get; init; }

    public required CacheProvenance Cache { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }
}

/// <summary>
/// Produces typed semantic evidence from message content. Implementations must never return
/// an action, and must never let message content influence their own configuration.
/// </summary>
public interface ISemanticMailClassifier
{
    ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// The single entry point for assessing a message. Implementations compose deterministic
/// extraction, semantic classification, profile comparison and policy, but the caller sees
/// one assessment.
/// </summary>
public interface IMailAssessor
{
    /// <summary>
    /// Assesses one message.
    /// </summary>
    /// <param name="callerSuppliedRawMessage">
    /// The original bytes, for a caller that holds them and whose envelope names no durable payload.
    /// Null when the caller has no bytes to offer, which is not the same as offering none.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The bytes are an option and the assessment is not a mode.</b> An implementation's first
    /// source is the envelope's durable payload, and this parameter is read only when that resolves
    /// to nothing. A caller supplying bytes is not asking a different question, it is supplying the
    /// one input the assessor would otherwise have read for itself, so the evidence produced must be
    /// the evidence the durable path would have produced.
    /// </para>
    /// <para>
    /// <b>Precedence, in order.</b> The durable payload when it resolves; then these bytes; then the
    /// existing "no original payload was available" branch, unchanged. That third branch stays
    /// reachable on purpose. It is what makes an outage visible, and a caller that quietly covered
    /// for a missing payload would turn a visible failure into an assessment no reader can tell was
    /// made without the original.
    /// </para>
    /// <para>
    /// <b>Nothing is recorded for this and nothing is compared.</b> Supplied bytes are read and
    /// dropped: an implementation must not spool them, must not treat them as a payload reference,
    /// and must not assert that they agree with whatever a spool holds. Branch one never consults
    /// this parameter, so the two sources are alternatives rather than a second opinion, and a check
    /// that they match would be a new failure mode on the path this exists to make work.
    /// </para>
    /// <para>
    /// Optional so that every existing call site keeps compiling, and trailing because a defaulted
    /// parameter is only reachable through this interface: an implementor must still declare it,
    /// which is deliberate, since a caller's need for it is a property of the caller.
    /// </para>
    /// </remarks>
    ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? callerSuppliedRawMessage = null);
}

/// <summary>
/// The entry point for assessing a chat message.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sibling of <see cref="IMailAssessor"/> rather than a widening of it</b>, because input is
/// per channel and output is shared: the two take different records and both produce a
/// <see cref="MailAssessment"/>.
/// </para>
/// <para>
/// <b>Implementations assess and take no action.</b> Every intervention on a chat channel is
/// post-hoc, and a caller cannot tell whether one was taken from this contract, so an implementation
/// records what it would have done rather than doing it.
/// </para>
/// </remarks>
public interface IChatAssessor
{
    ValueTask<MailAssessment> AssessAsync(
        ChatAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken);
}
