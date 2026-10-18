using StyloMail.Core;
using StyloMail.Queue;

namespace StyloMail.Host.Contracts;

/// <summary>One recipient awaiting review, with its stored deadline.</summary>
public sealed record ReviewRecipientResponse
{
    public required string Recipient { get; init; }

    public required DeliveryState State { get; init; }

    public DateTimeOffset? ReviewDeadline { get; init; }

    /// <summary>True when the deadline has passed and the sweep has not yet escalated it.</summary>
    public required bool Overdue { get; init; }

    public required int Attempts { get; init; }

    public string? LastError { get; init; }

    public static ReviewRecipientResponse From(QueueRecipientState recipient, DateTimeOffset now) => new()
    {
        Recipient = recipient.Recipient,
        State = recipient.State,
        ReviewDeadline = recipient.ReviewDeadline,
        Overdue = recipient.State == DeliveryState.Review
            && recipient.ReviewDeadline is { } deadline
            && deadline <= now,
        Attempts = recipient.Attempts,
        LastError = recipient.LastError,
    };
}

/// <summary>One message awaiting a human review decision.</summary>
public sealed record ReviewItemResponse
{
    public required string QueueId { get; init; }

    public required string TenantId { get; init; }

    public required string InternalMessageId { get; init; }

    public required IReadOnlyList<ReviewRecipientResponse> Recipients { get; init; }

    /// <summary>
    /// The pack decision that opened this review. Null means no pack was active; it is never
    /// rendered as Permit.
    /// </summary>
    public PolicyDecisionRecord? Policy { get; init; }

    /// <summary>
    /// True when the ledger has no assessment for this message at all, which is an integrity
    /// anomaly rather than "no pack was active". The surface must name it, not render Permit.
    /// </summary>
    public required bool PolicyRecordMissing { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One page of open reviews.</summary>
public sealed record ReviewListResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<ReviewItemResponse> Items { get; init; }

    public string? NextCursor { get; init; }

    public bool HasMore => NextCursor is not null;
}

/// <summary>A reviewed item with its audit history.</summary>
public sealed record ReviewDetailResponse
{
    public required ReviewItemResponse Item { get; init; }

    public required IReadOnlyList<ReviewEventResponse> Events { get; init; }
}

/// <summary>One recorded review transition.</summary>
public sealed record ReviewEventResponse
{
    public required string EventId { get; init; }

    public required string Recipient { get; init; }

    public string? AssessmentId { get; init; }

    public required string Event { get; init; }

    public required DeliveryState FromState { get; init; }

    public required DeliveryState ToState { get; init; }

    public required string Actor { get; init; }

    public required string Reason { get; init; }

    public string? PolicyReference { get; init; }

    public required DateTimeOffset At { get; init; }

    public static ReviewEventResponse From(QueueReviewEvent @event) => new()
    {
        EventId = @event.EventId,
        Recipient = @event.Recipient,
        AssessmentId = @event.AssessmentId,
        Event = @event.Event,
        FromState = @event.FromState,
        ToState = @event.ToState,
        Actor = @event.Actor,
        Reason = @event.Reason,
        PolicyReference = @event.PolicyReference,
        At = @event.At,
    };
}

/// <summary>A review resolution. The reason is required and recorded with the actor.</summary>
public sealed record ReviewResolutionRequest
{
    public string? Reason { get; init; }
}
