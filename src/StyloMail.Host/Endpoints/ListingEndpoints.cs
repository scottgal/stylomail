using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Controls;
using StyloMail.Queue;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The two read listings the operator console is built on: <c>GET /v1/senders</c> and
/// <c>GET /v1/messages</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are <b>tenant-scoped from the authenticated principal</b>, never from a parameter, and both
/// require <c>Review</c> — a separate grant from sending. A sender cannot enumerate the other
/// principals it shares a tenant with, nor list the mail in flight, through these routes; that
/// separation is the same one the decision ledger already keeps.
/// </para>
/// <para>
/// <b>Cross-tenant reads are absent rather than forbidden.</b> Neither route takes a tenant, so
/// there is no parameter to refuse: a caller sees exactly its own tenant's rows and never learns
/// whether another tenant exists. That is the stronger answer and the one the rest of the surface
/// already gives.
/// </para>
/// </remarks>
internal static class ListingEndpoints
{
    /// <summary>
    /// The states <c>GET /v1/messages</c> accepts, mapped onto the queue's own filters.
    /// </summary>
    /// <remarks>
    /// <b>Only the dispositions the queue actually enumerates by.</b> The queue's listing answers
    /// "what needs attention", not "what has been accepted" — there is no filter for messages in
    /// normal delivery, and adding one is not this route's to invent. Accepting a <c>queued</c>
    /// parameter and filtering the page after it had been cut would be worse than refusing it: pages
    /// would come back short or empty, <c>hasMore</c> would be wrong, and a caller paging through a
    /// console would see mail disappear.
    /// </remarks>
    private static readonly Dictionary<string, QueueListingFilter> Filters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["awaiting_decision"] = QueueListingFilter.AwaitingDecision,
            ["held"] = QueueListingFilter.Held,
            ["quarantined"] = QueueListingFilter.Quarantined,
        };

    internal static async Task<IResult> ListSendersAsync(
        ClaimsPrincipal user,
        PrincipalDirectory principals,
        ISenderControlStore controls,
        ISenderProfileStore profiles,
        CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId()!;

        // Both sources, with provenance, and only the rows that can actually authenticate. The
        // precedence rule decides which of a duplicated name survives, so this route does not have
        // to know about it.
        var senders = principals.SendersForTenant(tenantId);
        var controlStates = await controls.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);

        // One query, not one per sender: the label and the company are on the row so the sidebar
        // groups without a settings call for each.
        var senderProfiles = await profiles.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);

        return Results.Ok(SenderListingResponse.From(tenantId, senders, controlStates, senderProfiles));
    }

    internal static async Task<IResult> ListMessagesAsync(
        string? state,
        int? limit,
        string? after,
        ClaimsPrincipal user,
        QueueStore queue,
        CancellationToken cancellationToken)
    {
        var requested = string.IsNullOrWhiteSpace(state) ? "awaiting_decision" : state;

        if (!Filters.TryGetValue(requested, out var filter))
        {
            // Named refusals, not a silent fallback to the default. A caller asking for a disposition
            // this host does not enumerate has to be told, because the alternative is a console page
            // that quietly shows different mail from what its filter claims.
            return EndpointResults.Invalid(
                "unknown_state",
                $"'{requested}' is not a state this host lists. Supported: "
                + string.Join(", ", Filters.Keys.OrderBy(k => k, StringComparer.Ordinal))
                + ". Messages in normal delivery are not enumerable here; this lists what is awaiting "
                + "a decision.");
        }

        var page = await queue
            .ListAsync(
                new QueueListingQuery
                {
                    // From the principal, never from the query string. There is no tenant parameter to
                    // get wrong, which is why a cross-tenant read is absent rather than refused.
                    TenantId = user.TenantId()!,
                    Filter = filter,

                    // Clamped by the queue rather than rejected here, so an over-large request
                    // degrades into paging instead of an error.
                    Limit = limit ?? QueueListingLimits.DefaultPageSize,
                    After = after,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(MessageListingResponse.From(user.TenantId()!, requested.ToLowerInvariant(), page));
    }
}
