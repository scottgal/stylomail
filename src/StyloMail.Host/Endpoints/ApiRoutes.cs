using StyloMail.Host.Auth;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// The tenant-scoped HTTP surface (spec §12).
/// </summary>
/// <remarks>
/// Route shapes and their authorization requirements live together here so that the privilege
/// required by a route is visible at the point the route is declared, rather than in a filter
/// somewhere the reader has to go and find.
/// </remarks>
public static class ApiRoutes
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapPost("/assessments", AssessmentsEndpoints.AssessAsync)
            .RequireAuthorization(HostPolicies.Assess);

        v1.MapPost("/submissions", SubmissionsEndpoints.SubmitAsync)
            .RequireAuthorization(HostPolicies.Send);

        // Send *or* Review. A reviewer can release a quarantined message by this same queue id, and
        // releasing is strictly more than reading it, so requiring the lesser capability would let a
        // reviewer act on a message they were not permitted to inspect. See
        // HostPolicies.SendOrReview: this is the only route that accepts two privileges.
        v1.MapGet("/submissions/{id}", SubmissionsEndpoints.GetAsync)
            .RequireAuthorization(HostPolicies.SendOrReview);

        v1.MapGet("/decisions", DecisionsEndpoints.ListAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/conversations/history", ConversationHistoryEndpoints.ListAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/decisions/{id}", DecisionsEndpoints.GetAsync)
            .RequireAuthorization(HostPolicies.Review);

        // The two listings the operator console reads. `Review` for both: enumerating a tenant's
        // principals or its messages in flight is reviewer-level visibility, and neither grants the
        // ability to act: the pause and resume routes below remain `Administer`.
        v1.MapGet("/senders", ListingEndpoints.ListSendersAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/messages", ListingEndpoints.ListMessagesAsync)
            .RequireAuthorization(HostPolicies.Review);

        // Operator metadata. Read on `Review`, write on `Administer`: the split the pause route
        // already draws, and neither grants an effect on mail.
        v1.MapGet("/senders/{id}/settings", ManagementEndpoints.GetSenderSettingsAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPut("/senders/{id}/settings", ManagementEndpoints.PutSenderSettingsAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapGet("/companies", ManagementEndpoints.ListCompaniesAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/companies", ManagementEndpoints.CreateCompanyAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPut("/companies/{id}", ManagementEndpoints.UpdateCompanyAsync)
            .RequireAuthorization(HostPolicies.Administer);

        // Entity properties. Reading the effective binding is reviewer-level visibility; setting,
        // confirming or clearing it changes what policy will do and is an administrative act.
        // Confirmation sits on the write side deliberately: it is a recorded human assertion.
        v1.MapGet("/properties/{kind}/{id}", PropertyEndpoints.GetEffectiveAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/properties/{kind}/{id}/{key}/history", PropertyEndpoints.GetHistoryAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPut("/properties/{kind}/{id}/{key}", PropertyEndpoints.SetAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/properties/{kind}/{id}/{key}/confirm", PropertyEndpoints.ConfirmAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapDelete("/properties/{kind}/{id}/{key}", PropertyEndpoints.ClearAsync)
            .RequireAuthorization(HostPolicies.Administer);

        // Profile surface. Read-only and reviewer-level: the catalogue of versioned KPI definitions,
        // and one entity/domain/company/conversation profile with its coverage and named absences.
        v1.MapGet("/kpis", ProfileEndpoints.GetKpis)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/profiles/{kind}/{id}", ProfileEndpoints.GetProfileAsync)
            .RequireAuthorization(HostPolicies.Review);

        // The evidence drawer. Reviewer-level and content-free: what a KPI was computed from, and
        // what a stored evidence reference resolves to. No body, address, filename or subject.
        v1.MapGet("/evidence/kpi/{definitionId}/{kind}/{id}", EvidenceEndpoints.GetKpiEvidenceAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/evidence/reference", EvidenceEndpoints.GetReferenceAsync)
            .RequireAuthorization(HostPolicies.Review);

        // Policy administration. Reading a pack, its versions or a binding is reviewer visibility;
        // drafting, approving, activating, withdrawing, binding and revoking change what policy
        // will do and are administrative. The capability catalogue is static and reads no pack.
        v1.MapGet("/policy/packs", PolicyEndpoints.ListPacksAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/policy/packs/{id}", PolicyEndpoints.GetPackAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/policy/packs", PolicyEndpoints.DraftPackAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/policy/packs/{id}/{version}/approve", PolicyEndpoints.ApprovePackAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/policy/packs/{id}/{version}/activate", PolicyEndpoints.ActivatePackAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/policy/packs/{id}/{version}/withdraw", PolicyEndpoints.WithdrawPackAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/policy/packs/{id}/{version}/deactivate", PolicyEndpoints.DeactivatePackAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapGet("/policy/bindings", PolicyEndpoints.ListBindingsAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/policy/bindings", PolicyEndpoints.CreateBindingAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/policy/bindings/{id}/revoke", PolicyEndpoints.RevokeBindingAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapGet("/policy/effective-binding", PolicyEndpoints.EffectiveBindingAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/policy/capabilities", PolicyEndpoints.Capabilities)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/policy/status", PolicyEndpoints.StatusAsync)
            .RequireAuthorization(HostPolicies.Review);

        // Review queue. Reads and the reversible decisions are Review; Reject is a permanent
        // refusal and requires Administer. Every mutation takes a reason in the body.
        v1.MapGet("/policy/reviews", ReviewEndpoints.ListAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapGet("/policy/reviews/{queueId}", ReviewEndpoints.GetAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/policy/reviews/{queueId}/release", ReviewEndpoints.ReleaseAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/policy/reviews/{queueId}/quarantine", ReviewEndpoints.QuarantineAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/policy/reviews/{queueId}/reject", ReviewEndpoints.RejectAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/feedback", FeedbackEndpoints.SubmitAsync)
            .RequireAuthorization(HostPolicies.Feedback);

        v1.MapPost("/quarantine/{id}/release", QuarantineEndpoints.ReleaseAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/controls/senders/{id}/pause", ControlsEndpoints.PauseSenderAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/controls/senders/{id}/resume", ControlsEndpoints.ResumeSenderAsync)
            .RequireAuthorization(HostPolicies.Administer);

        // The access-proxy management surface. Read on Review, mutate on Administer; mapped from its
        // own module so the proxy routes and their privilege split read together.
        ProxyEndpoints.Map(v1);
    }
}
