using StyloMail.Host.Feedback;

namespace StyloMail.Host.Contracts;

/// <summary>Body of <c>POST /v1/feedback</c>.</summary>
public sealed record FeedbackRequest
{
    public required string DecisionId { get; init; }

    public FeedbackLabel Label { get; init; }

    /// <summary>
    /// Nullable rather than defaulted. A missing scope is a client error, not an invitation to
    /// pick a blast radius on the caller's behalf.
    /// </summary>
    public FeedbackScope? Scope { get; init; }

    /// <summary>The recipient or sender identity the scope binds to.</summary>
    public string? Recipient { get; init; }

    public string? Note { get; init; }
}

/// <summary>Answer to <c>POST /v1/feedback</c>.</summary>
public sealed record FeedbackResponse
{
    public required string FeedbackId { get; init; }

    public required string DecisionId { get; init; }

    public required FeedbackScope Scope { get; init; }

    public required FeedbackLabel Label { get; init; }

    public string? Recipient { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Body of <c>POST /v1/controls/senders/{id}/pause</c>.</summary>
public sealed record PauseSenderRequest
{
    public string? Reason { get; init; }
}

/// <summary>Answer to <c>POST /v1/controls/senders/{id}/pause</c>.</summary>
public sealed record PauseSenderResponse
{
    public required string PrincipalId { get; init; }

    public required bool Paused { get; init; }

    public required string UpdatedBy { get; init; }
}

/// <summary>Answer to <c>POST /v1/controls/senders/{id}/resume</c>.</summary>
public sealed record ResumeSenderResponse
{
    public required string PrincipalId { get; init; }

    public required bool Paused { get; init; }

    public required string UpdatedBy { get; init; }
}

/// <summary>Answer to <c>POST /v1/quarantine/{id}/release</c>.</summary>
public sealed record QuarantineReleaseResponse
{
    public required string QueueId { get; init; }

    /// <summary>True when this call performed the release; false when it was already released.
    /// Either way the message is not quarantined, which is what the caller asked for.</summary>
    public required bool Released { get; init; }

    public required string ReleasedBy { get; init; }
}
