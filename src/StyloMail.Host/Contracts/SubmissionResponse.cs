using StyloMail.Core;
using StyloMail.Queue;

namespace StyloMail.Host.Contracts;

/// <summary>
/// The answer to <c>POST /v1/submissions</c>.
/// </summary>
/// <remarks>
/// <see cref="QueueId"/> is present if and only if delivery responsibility transferred. It is not
/// accompanied by a boolean saying so, because a boolean is a second source of truth that can
/// disagree with the id, and the disagreement would tell a caller their mail was accepted when
/// nothing durable exists.
/// </remarks>
public sealed record SubmissionResponse
{
    public required string QueueId { get; init; }

    /// <summary><c>Accepted</c> for a first submission, <c>Duplicate</c> for an idempotent replay.</summary>
    public required string Status { get; init; }

    /// <summary>The decision this submission produced. Absent on a replay, which produced none.</summary>
    public string? AssessmentId { get; init; }

    public required IReadOnlyList<SubmissionRecipientResponse> Recipients { get; init; }

    public static SubmissionResponse FromAssessment(string queueId, MailAssessment assessment, string status) => new()
    {
        QueueId = queueId,
        Status = status,
        AssessmentId = assessment.AssessmentId,
        Recipients = [.. assessment.RecipientDispositions.Select(d => new SubmissionRecipientResponse
        {
            Recipient = d.Recipient,
            DeliveryState = d.DeliveryState,
            Action = d.Action,
            ReEvaluateBy = d.ReEvaluateBy,
        })],
    };

    public static SubmissionResponse FromQueueItem(QueueItem item, string status) => new()
    {
        QueueId = item.QueueId,
        Status = status,
        Recipients = [.. item.Recipients.Select(r => new SubmissionRecipientResponse
        {
            Recipient = r.Recipient,
            DeliveryState = r.State,
            ReEvaluateBy = r.ReEvaluateBy,
        })],
    };
}

/// <summary>One recipient of a submitted message.</summary>
/// <remarks>
/// <see cref="Action"/> is null when the answer came from queue state alone, a replay serves a
/// message the queue already holds, and the policy action that put it there belongs to the
/// original decision rather than to this response.
/// </remarks>
public sealed record SubmissionRecipientResponse
{
    public required string Recipient { get; init; }

    public required DeliveryState DeliveryState { get; init; }

    public MailAction? Action { get; init; }

    public DateTimeOffset? ReEvaluateBy { get; init; }
}

/// <summary>
/// The answer to <c>GET /v1/submissions/{id}</c>: recipient-level disposition and progress.
/// </summary>
public sealed record SubmissionStatusResponse
{
    public required string QueueId { get; init; }

    /// <summary>
    /// StyloMail's own message identifier.
    /// </summary>
    /// <remarks>
    /// <b>This is the join key to the decision ledger, and it is the reason this field exists.</b> A
    /// reviewer looking at a quarantined message needs the explanation for it, and the ledger is
    /// keyed by assessment id — which appeared only on the <c>POST /v1/submissions</c> response, a
    /// response a reviewer working from a list never saw. The ledger also stores the message id, so
    /// exposing it here gives a path from a listed message to its decision without either lane
    /// changing a schema: <c>GET /v1/decisions?messageId=…</c>.
    ///
    /// <para>
    /// Safe to expose because it is already on <c>DecisionResponse</c>, which is served to the same
    /// <c>Review</c>-holding caller. It is our own opaque identifier, not message content.
    /// </para>
    /// </remarks>
    public required string InternalMessageId { get; init; }

    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Null when the payload bytes have been purged; the metadata row survives.</summary>
    public DateTimeOffset? PurgedAt { get; init; }

    public required IReadOnlyList<SubmissionRecipientProgress> Recipients { get; init; }

    public static SubmissionStatusResponse From(QueueItem item) => new()
    {
        QueueId = item.QueueId,
        InternalMessageId = item.Envelope.InternalMessageId,
        State = item.State,
        Attempts = item.Attempts,
        NextAttemptAt = item.NextAttemptAt,
        ExpiresAt = item.ExpiresAt,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        PurgedAt = item.PurgedAt,
        Recipients = [.. item.Recipients.Select(r => new SubmissionRecipientProgress
        {
            Recipient = r.Recipient,
            State = r.State,
            Attempts = r.Attempts,
            LastAttemptAt = r.LastAttemptAt,
            DeliveredAt = r.DeliveredAt,
            ReEvaluateBy = r.ReEvaluateBy,
        })],
    };
}

/// <summary>
/// One recipient's delivery progress.
/// </summary>
/// <remarks>
/// Recipient-scoped by construction. Two recipients of the same message are very often in
/// different states, and reporting a transaction-level answer would hide that, or worse, leak
/// one recipient's outcome into another's view.
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
