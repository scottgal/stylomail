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

        v1.MapPost("/feedback", FeedbackEndpoints.SubmitAsync)
            .RequireAuthorization(HostPolicies.Feedback);

        v1.MapPost("/quarantine/{id}/release", QuarantineEndpoints.ReleaseAsync)
            .RequireAuthorization(HostPolicies.Review);

        v1.MapPost("/controls/senders/{id}/pause", ControlsEndpoints.PauseSenderAsync)
            .RequireAuthorization(HostPolicies.Administer);

        v1.MapPost("/controls/senders/{id}/resume", ControlsEndpoints.ResumeSenderAsync)
            .RequireAuthorization(HostPolicies.Administer);
    }
}
