using System.Text;
using StyloMail.Host.Observability;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>/health/live</c>, <c>/health/ready</c> and <c>/metrics</c> — unauthenticated by necessity,
/// and therefore carrying no sensitive content at all.
/// </summary>
/// <remarks>
/// These routes are mapped outside the <c>/v1</c> group and without an authorization requirement:
/// a probe that has to authenticate cannot do its job, and a credential distributed to a load
/// balancer is a credential in one more place. The compensation is that nothing here may describe
/// a message, an identity or a tenant.
/// </remarks>
public static class HealthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", LivenessAsync);
        app.MapGet("/health/ready", ReadinessAsync);
        app.MapGet("/metrics", MetricsAsync);
    }

    /// <summary>Is the process up? Deliberately checks nothing else — a dependency outage is not a
    /// reason to have a container restarted.</summary>
    private static Task<IResult> LivenessAsync()
        => Task.FromResult<IResult>(Results.Ok(new { status = "live" }));

    /// <summary>Can this host durably accept mail right now?</summary>
    private static Task<IResult> ReadinessAsync(ReadinessProbe probe)
    {
        var result = probe.Check();

        return Task.FromResult<IResult>(result.Ready
            ? Results.Ok(new { status = "ready" })
            : Results.Json(
                new { status = "not_ready", failedChecks = result.FailedChecks },
                statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    private static Task<IResult> MetricsAsync(HostMetrics metrics)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);
        metrics.Render(writer);

        return Task.FromResult<IResult>(Results.Text(builder.ToString(), "text/plain; version=0.0.4"));
    }
}
