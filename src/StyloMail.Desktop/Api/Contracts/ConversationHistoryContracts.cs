namespace StyloMail.Desktop.Api.Contracts;

/// <summary>
/// One bounded read of assessed events in a keyed conversation. The cursor is
/// opaque and must be replayed with the same filter values.
/// </summary>
public sealed record ConversationHistoryQuery
{
    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    public string? ThreadId { get; init; }

    /// <summary>Inclusive UTC lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive UTC upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    public int Limit { get; init; } = 25;

    /// <summary>The previous response's cursor, passed through unchanged.</summary>
    public string? After { get; init; }
}

/// <summary>
/// A page of original assessed decisions for one authenticated tenant's
/// workspace/channel and optional thread. No trend aggregate is derived here.
/// </summary>
public sealed record ConversationHistoryResponse
{
    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    public required string? ThreadId { get; init; }

    public required DateTimeOffset? From { get; init; }

    public required DateTimeOffset? To { get; init; }

    public required IReadOnlyList<DecisionResponse> Decisions { get; init; }

    public required string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public required int SkippedCount { get; init; }
}
