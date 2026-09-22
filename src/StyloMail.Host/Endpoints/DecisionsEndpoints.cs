using System.Security.Claims;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Host.Decisions;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>GET /v1/decisions/{id}</c> — evidence, reasons, versions and coverage.
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
}
