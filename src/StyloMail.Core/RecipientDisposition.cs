namespace StyloMail.Core;

/// <summary>Where a single recipient's copy of a message has got to.</summary>
public enum DeliveryState
{
    Queued = 0,
    Held = 1,
    Quarantined = 2,
    Delivering = 3,
    Delivered = 4,
    RetryScheduled = 5,
    TerminalFailure = 6,
}

/// <summary>
/// The disposition for one recipient of one message.
/// </summary>
/// <remarks>
/// Dispositions are per recipient, never per transaction. A multi-recipient submission persists
/// each recipient's outcome, retries only the pending ones, and never rejects an entire SMTP
/// transaction while silently retaining a subset. Where the downstream can only give a
/// transaction-level answer, that limitation is recorded rather than hidden.
///
/// <para>
/// <b>Isolation:</b> one recipient's risk here is scoped to that recipient. A recipient's
/// relationship history, address and contact graph are never exposed to another recipient,
/// including through reason text.
/// </para>
/// </remarks>
public sealed record RecipientDisposition
{
    public required string Recipient { get; init; }

    /// <summary>Recipient-scoped risk. Distinct from the message-level <c>RiskIndex</c>.</summary>
    public required double RecipientRisk { get; init; }

    public required MailAction Action { get; init; }

    public required DeliveryState DeliveryState { get; init; }

    /// <summary>Deadline after which a hold is re-evaluated. A hold deadline never extends silently forever.</summary>
    public DateTimeOffset? ReEvaluateBy { get; init; }

    /// <summary>Signal ids that applied only to this recipient (novelty, relationship, preference).</summary>
    public IReadOnlyList<string>? RecipientScopedSignalIds { get; init; }
}
