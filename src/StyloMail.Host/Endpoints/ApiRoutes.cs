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

        v1.MapGet("/submissions/{id}", SubmissionsEndpoints.GetAsync)
            .RequireAuthorization(HostPolicies.Send);

        v1.MapGet("/decisions/{id}", DecisionsEndpoints.GetAsync)
            .RequireAuthorization(HostPolicies.Review);

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
