namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// <c>GET /v1/submissions/{id}</c>: recipient-level disposition and progress.
/// </summary>
/// <remarks>
/// Read from the queue's own state rather than from a decision, which is why
/// there is no action here: what policy decided belongs to the decision that
/// put the message in the queue, and the ledger is where it is read.
/// </remarks>
public sealed record SubmissionStatusResponse
{
    public required string QueueId { get; init; }

    /// <summary>
    /// The join key from this message to its decisions.
    /// </summary>
    /// <remarks>
    /// <c>GET /v1/decisions?messageId=</c> takes this and returns the decisions
    /// recorded against the message. It is the console's headline flow: a
    /// reviewer looking at a quarantined message can reach the explanation for
    /// it, which was impossible until this field existed on both listings.
    ///
    /// <para>
    /// An id per message rather than an assessment id per row, because a
    /// message can legitimately be assessed more than once and a single id
    /// could only hold one of them.
    /// </para>
    /// </remarks>
    public required string InternalMessageId { get; init; }

    /// <summary>The transaction-level state. Recipients can and often do differ from it.</summary>
    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Null when the payload bytes have been purged; the metadata row survives.</summary>
    public DateTimeOffset? PurgedAt { get; init; }

    public required IReadOnlyList<SubmissionRecipientProgress> Recipients { get; init; }
}

/// <summary>
/// One recipient's delivery progress.
/// </summary>
/// <remarks>
/// <b>Per recipient, never per transaction.</b> Two recipients of the same
/// message are very often in different states, and the console must show that
/// rather than a single row: collapsing them would hide the case that matters,
/// where one recipient's copy is held and another's has gone.
/// </remarks>
public sealed record SubmissionRecipientProgress
{
    public required string Recipient { get; init; }

    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }
}

/// <summary><c>POST /v1/quarantine/{id}/release</c>.</summary>
public sealed record QuarantineReleaseResponse
{
    public required string QueueId { get; init; }

    /// <summary>
    /// True when this call performed the release; false when it was already
    /// released. Both mean the message is not quarantined, which is what was
    /// asked for, so neither is an error.
    /// </summary>
    public required bool Released { get; init; }

    /// <summary>The actor recorded in the audit trail. Never supplied by this client.</summary>
    public required string ReleasedBy { get; init; }
}

/// <summary>Body of <c>POST /v1/controls/senders/{id}/pause</c>.</summary>
public sealed record PauseSenderRequest
{
    /// <summary>The audit record for the pause. The console requires one.</summary>
    public string? Reason { get; init; }
}

/// <summary><c>POST /v1/controls/senders/{id}/pause</c>.</summary>
public sealed record PauseSenderResponse
{
    public required string PrincipalId { get; init; }

    public required bool Paused { get; init; }

    /// <summary>Who applied it, from the authenticated principal. The client cannot choose this.</summary>
    public required string UpdatedBy { get; init; }
}

/// <summary><c>POST /v1/controls/senders/{id}/resume</c>.</summary>
public sealed record ResumeSenderResponse
{
    public required string PrincipalId { get; init; }

    public required bool Paused { get; init; }

    public required string UpdatedBy { get; init; }
}

/// <summary>Body of <c>POST /v1/feedback</c>.</summary>
public sealed record FeedbackRequest
{
    public required string DecisionId { get; init; }

    public FeedbackLabel Label { get; init; }

    /// <summary>
    /// Nullable rather than defaulted, mirroring the Host: a missing scope is a
    /// client error, not an invitation to pick a blast radius on the caller's
    /// behalf. The console always sets it.
    /// </summary>
    public FeedbackScope? Scope { get; init; }

    /// <summary>The recipient or sender identity the scope binds to.</summary>
    public string? Recipient { get; init; }

    public string? Note { get; init; }
}

/// <summary><c>POST /v1/feedback</c>.</summary>
public sealed record FeedbackResponse
{
    public required string FeedbackId { get; init; }

    public required string DecisionId { get; init; }

    public required FeedbackScope Scope { get; init; }

    public required FeedbackLabel Label { get; init; }

    public string? Recipient { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}
