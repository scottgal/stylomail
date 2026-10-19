using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Profiles;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The dashboard's read-only aggregates: mail flow, queue attention counts and storage sizes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read on <c>Review</c>.</b> These routes aggregate a tenant's mail, queue and storage, which is
/// reviewer-level visibility, and none of them can act on anything. They take no tenant parameter:
/// the tenant is the authenticated principal's, so a cross-tenant read is absent rather than refused.
/// </para>
/// <para>
/// <b>A storage failure is a 503, never a calm zero.</b> A dashboard that renders "0 messages" while
/// the database is unreadable is worse than one that says it cannot read. Partial source failures
/// are answered 200 with per-series absence, because the half that was measured is still true.
/// </para>
/// </remarks>
internal static class SummaryEndpoints
{
    /// <summary><c>GET /v1/overview/flow</c>.</summary>
    internal static IResult GetFlow(
        string? window,
        ClaimsPrincipal user,
        DashboardSummaryStore store,
        CancellationToken cancellationToken)
    {
        var parsed = FlowWindow.Parse(window);

        if (parsed is null)
        {
            return EndpointResults.Invalid(
                "unknown_window",
                $"'{window}' is not a flow window. Supported: last_24_hours, last_7_days.");
        }

        try
        {
            return Results.Ok(store.ReadFlow(user.TenantId()!, parsed, cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The queue and the decision ledger could not be read, so no flow is being reported.");
        }
    }

    /// <summary><c>GET /v1/queues/summary</c>.</summary>
    internal static IResult GetQueues(
        ClaimsPrincipal user,
        DashboardSummaryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(store.ReadQueues(user.TenantId()!, cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The queue could not be read, so no queue counts are being given.");
        }
    }

    /// <summary><c>GET /v1/storage/summary</c>.</summary>
    internal static IResult GetStorage(
        ClaimsPrincipal user,
        DashboardSummaryStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(store.ReadStorage(user.TenantId()!, cancellationToken));
        }
        catch (StorageUnavailableException)
        {
            return EndpointResults.StorageUnavailable(
                "The storage metadata could not be read, so no byte totals are being given.");
        }
    }
}
