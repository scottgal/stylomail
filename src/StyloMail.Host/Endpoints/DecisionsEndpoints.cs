using System.Security.Claims;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>GET /v1/decisions/{id}</c>, evidence, reasons, versions and coverage.
/// </summary>
/// <remarks>
/// Reading the ledger requires the review privilege, which is a separate grant from sending. A
/// sender cannot read back the decisions about their own mail through this route: reviewing is a
/// distinct role, and collapsing it into "whoever sent the message" would remove the separation
/// the spec asks for.
/// </remarks>
internal static class DecisionsEndpoints
{
    internal static async Task<IResult> GetAsync(
        string id,
        ClaimsPrincipal user,
        IDecisionLedger ledger,
        CancellationToken cancellationToken)
    {
        var assessment = await ledger.FindAsync(user.TenantId()!, id, cancellationToken);

        // Not found and not-yours are the same answer. Distinguishing them would let a tenant
        // confirm which ids exist elsewhere by asking for them.
        return assessment is null
            ? EndpointResults.NotFound(
                "decision_not_found",
                "No decision with that identifier is recorded for this tenant.")
            : Results.Ok(DecisionResponse.From(assessment));
    }

    /// <summary>
    /// <c>GET /v1/decisions</c> — one page of the ledger, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same <c>Review</c> privilege as reading a single decision, and the same scoping rule: the
    /// tenant comes from the authenticated principal and there is **no tenant parameter**, so a
    /// cross-tenant read is absent rather than forbidden. There is nothing for a caller to name and
    /// nothing to refuse.
    /// </para>
    /// <para>
    /// <b>Rows are summaries; the evidence is one request away.</b> See
    /// <see cref="DecisionListingResponse"/> for why the full decision is not inlined.
    /// </para>
    /// <para>
    /// <c>messageId</c> is how a reviewer gets from a quarantined message to the explanation for it:
    /// the message listings expose <c>internalMessageId</c>, and this returns every decision recorded
    /// against it. A list rather than one decision, because a message can legitimately be assessed
    /// more than once and returning only the newest would hide that.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> ListAsync(
        string? action,
        string? messageId,
        int? limit,
        string? after,
        ClaimsPrincipal user,
        IDecisionLedger ledger,
        CancellationToken cancellationToken)
    {
        MailAction? filter = null;

        if (!string.IsNullOrWhiteSpace(action))
        {
            if (!Enum.TryParse<MailAction>(action, ignoreCase: true, out var parsed))
            {
                // Refused by name, like the message listing's unknown state. A filter that silently
                // fell back to "no filter" would show a reviewer every action while the response
                // said they were looking at one — the same failure as a page that lies about being
                // complete, one layer up.
                return EndpointResults.Invalid(
                    "unknown_action",
                    $"'{action}' is not an action this ledger records. Supported: "
                    + string.Join(", ", Enum.GetNames<MailAction>().Order(StringComparer.Ordinal))
                    + ".");
            }

            filter = parsed;
        }

        DecisionListingPage page;

        try
        {
            page = await ledger
                .ListAsync(
                    new DecisionListingQuery
                    {
                        // From the principal, never from the query string.
                        TenantId = user.TenantId()!,
                        Action = filter,

                        // The link from a listed message to its decision. An equality on an indexed
                        // column, so it narrows the query rather than the page.
                        InternalMessageId = messageId,

                        // Clamped by the ledger rather than rejected here, so an over-large request
                        // degrades into paging instead of an error.
                        Limit = limit ?? DecisionListingLimits.DefaultPageSize,
                        After = after,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDecisionCursorException ex)
        {
            // A cursor we did not issue is refused rather than treated as "start again". Silently
            // answering with the first page would leave a client paging in a loop with nothing
            // anywhere saying why.
            return EndpointResults.Invalid("invalid_cursor", ex.Message);
        }

        return Results.Ok(DecisionListingResponse.From(user.TenantId()!, filter, page));
    }
}
