namespace StyloMail.AccessProxy.Submission;

/// <summary>The only submission decisions that can affect the SMTP transaction.</summary>
public enum ProxySubmissionDisposition
{
    Allow = 0,
    Refuse = 1,
    SemanticUnavailable = 2,
}

/// <summary>An opaque pre-delivery assessment result.</summary>
public sealed record ProxySubmissionAssessmentResult
{
    public required ProxySubmissionDisposition Disposition { get; init; }

    /// <summary>Only deterministic policy findings are allowed to refuse submission.</summary>
    public required bool IsDeterministic { get; init; }

    public string? SubmissionId { get; init; }

    public string? AssessmentId { get; init; }

    public string? DecisionId { get; init; }
}

/// <summary>Assesses one staged submission locally before upstream DATA is accepted.</summary>
/// <remarks>
/// Semantic absence, delay or failure is represented as <see cref="ProxySubmissionDisposition.SemanticUnavailable"/>
/// and must relay. Only a deterministic refusal changes delivery.
/// </remarks>
public interface IProxySubmissionAssessment
{
    ValueTask<ProxySubmissionAssessmentResult> AssessAsync(
        IStagedProxySubmission submission,
        CancellationToken cancellationToken);
}

/// <summary>A bounded, disposable submission staging handle.</summary>
public interface IStagedProxySubmission : IAsyncDisposable
{
    /// <summary>The bounded staging stream. Writable until assessment begins, then rewound for reading.</summary>
    Stream Content { get; }

    long Length { get; }
}

/// <summary>Creates short-lived local staging handles for the DATA phase.</summary>
/// <remarks>
/// Implementations must delete terminal submissions in <see cref="IAsyncDisposable.DisposeAsync"/>,
/// apply a bounded TTL to abandoned/crashed handles, and provide startup cleanup. This is explicitly
/// not a mailbox, ledger payload, or durable evidence store.
/// </remarks>
public interface IEphemeralSubmissionStager
{
    /// <summary>Creates the one handle into which the SMTP DATA bytes are written exactly once.</summary>
    ValueTask<IStagedProxySubmission> CreateAsync(
        long maximumBytes,
        CancellationToken cancellationToken);

    ValueTask DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
