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
    /// internally generated value — an assessment id, for instance — is new on every attempt, so
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
    ValueTask<MailAssessment> AssessAsync(
        MailAnalysisInput input,
        AssessmentContext context,
        CancellationToken cancellationToken);
}
