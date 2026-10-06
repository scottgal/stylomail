using StyloMail.Host.Decisions;

namespace StyloMail.Host.Contracts;

/// <summary>A bounded page of original assessed decisions for one Slack conversation.</summary>
public sealed record ConversationHistoryResponse
{
    public required string TenantId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ChannelId { get; init; }

    /// <summary>Null when the page covers all threads in the channel.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Inclusive UTC lower bound used by the query.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive UTC upper bound used by the query.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Original per-assessment records. No values are aggregated across decisions.</summary>
    public required IReadOnlyList<DecisionResponse> Decisions { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }

    public required int SkippedCount { get; init; }

    public static ConversationHistoryResponse Create(
        ConversationHistoryQuery query,
        DecisionListingPage page) => new()
    {
        TenantId = query.TenantId,
        WorkspaceId = query.WorkspaceId,
        ChannelId = query.ChannelId,
        ThreadId = query.ThreadId,
        From = query.From?.ToUniversalTime(),
        To = query.To?.ToUniversalTime(),
        Decisions = [.. page.Items.Select(DecisionResponse.From)],
        NextCursor = page.NextCursor,
        HasMore = page.HasMore,
        SkippedCount = page.SkippedCount,
    };
}
