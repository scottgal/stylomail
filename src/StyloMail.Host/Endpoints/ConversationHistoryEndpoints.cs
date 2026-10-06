using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;

namespace StyloMail.Host.Endpoints;

/// <summary>Reads stored assessment evidence for a Slack channel or thread.</summary>
internal static class ConversationHistoryEndpoints
{
    internal static async Task<IResult> ListAsync(
        string? workspaceId,
        string? channelId,
        string? threadId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        string? after,
        ClaimsPrincipal user,
        IDecisionLedger ledger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return EndpointResults.Invalid("workspace_required", "A workspaceId is required.");
        }

        if (string.IsNullOrWhiteSpace(channelId))
        {
            return EndpointResults.Invalid("channel_required", "A channelId is required.");
        }

        if (threadId is not null && string.IsNullOrWhiteSpace(threadId))
        {
            return EndpointResults.Invalid("invalid_thread", "Omit threadId or provide a non-empty value.");
        }

        var normalizedFrom = from?.ToUniversalTime();
        var normalizedTo = to?.ToUniversalTime();
        if (normalizedFrom is { } lower && normalizedTo is { } upper && lower >= upper)
        {
            return EndpointResults.Invalid("invalid_time_range", "from must be earlier than the exclusive to bound.");
        }

        var query = new ConversationHistoryQuery
        {
            TenantId = user.TenantId()!,
            WorkspaceId = workspaceId,
            ChannelId = channelId,
            ThreadId = threadId,
            From = normalizedFrom,
            To = normalizedTo,
            Limit = limit ?? DecisionListingLimits.DefaultPageSize,
            After = after,
        };

        DecisionListingPage page;
        try
        {
            page = await ledger.ListConversationHistoryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDecisionCursorException ex)
        {
            return EndpointResults.Invalid("invalid_cursor", ex.Message);
        }

        return Results.Ok(ConversationHistoryResponse.Create(query, page));
    }
}
