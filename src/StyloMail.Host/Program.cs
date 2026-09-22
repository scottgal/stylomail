using Microsoft.Extensions.Options;
using StyloMail.Host.Auth;
using StyloMail.Host.Cli;
using StyloMail.Host.Endpoints;
using StyloMail.Host.Hosting;
using StyloMail.Host.Traffic;
using StyloMail.Transport.Cloudflare;

// One executable, two front ends. `serve` runs Kestrel; everything else is a one-shot command
// against the same composition root, so the CLI cannot drift from the routes it administers.
if (!CliApplication.TryParse(args, out var command, out var parseError))
{
    await Console.Error.WriteLineAsync(parseError);
    return 2;
}

if (command is not ServeCommand)
{
    return await CliApplication.RunAsync(args, Console.Out);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloMailHost(builder.Configuration);

var app = builder.Build();

// Schema before traffic: the host's tables, the persistence schema and the queue's, all in the
// one database file this deployment uses.
await HostServices.InitialiseStorageAsync(app.Services);

// Resolve the assessor at startup, not on first request.
//
// It is registered as a lazy singleton, so without this a half-configured deployment, one secret
// set and the other missing, would boot, report healthy on /health/ready, and only fail when real
// mail arrived. A misconfiguration that looks like a healthy service is the failure mode this
// whole check exists to prevent, and forcing the resolution here is what makes it fail at boot.
_ = app.Services.GetRequiredService<StyloMail.Core.IMailAssessor>();

// And the ingress sink, for the same reason plus one of its own: building it is where the two
// composition assertions run, the ingress bound against the queue's, and the sink's spool against
// the pipeline's. Both describe a property of a pair of components that neither can check alone, so
// the only good moment to discover a mismatch is before the host is serving.
_ = app.Services.GetRequiredService<StyloMail.Transport.Ingress.ISmtpIngressSink>();

// Say what this deployment will and will not do at the mail boundary. A listener writing into a
// queue nothing drains is a configuration mistake best discovered here rather than as messages
// ageing toward their expiry.
app.Logger.LogInformation("StyloMail transport: {Transport}", HostServices.DescribeTransport(app.Configuration));

// Explicit rather than relying on the implicit middleware WebApplication inserts: the
// authentication boundary is the thing this host is most worth reading carefully, so it should be
// visible in the pipeline rather than implied.
app.UseAuthentication();

// After authentication, because the channel is a property of the authenticated principal: the
// middleware only engages for requests that arrived on the cookie channel.
app.UseMiddleware<CsrfMiddleware>();

app.UseAuthorization();

ApiRoutes.Map(app);
HealthEndpoints.Map(app);
SessionEndpoints.Map(app, app.Services.GetRequiredService<PrincipalDirectory>());

// The Cloudflare intake is mapped only when the deployment has opted into it, and resolving the
// connector here is what makes an enabled-but-secretless configuration a startup failure rather
// than a route that answers 401 to every message the Worker offers.
var transport = app.Services.GetRequiredService<IOptions<HostTransportOptions>>().Value;

if (transport.CloudflareIngress.Enabled)
{
    _ = app.Services.GetRequiredService<CloudflareEmailRoutingConnector>();

    CloudflareIngressEndpoints.Map(app, transport.CloudflareIngress.MaxMessageBytes);
}

// The Slack events intake, on the same terms: mapped only when this deployment has configured it,
// so an unconfigured host answers 404 rather than offering a route nobody asked for. Validating
// here makes an enabled-but-identityless configuration a startup failure rather than an ingress that
// cannot recognise its own posts and would assess its own output in a loop.
var slack = app.Services.GetRequiredService<IOptions<SlackIngressOptions>>().Value;

if (slack.Enabled)
{
    slack.Validate();
    SlackEventsEndpoints.Map(app, SlackEventsEndpoints.MaxBodyBytes);
}

// The live-traffic hub, mapped on the same terms: only when this deployment has enabled it, so an
// unconfigured host answers 404 on the negotiate route rather than offering a feed nobody asked
// for. Review is the privilege, because a subscriber reads a tenant's traffic and nothing more:
// this route adds no capability, so it must not be the way one is acquired.
//
// The API key is presented in a header on both the negotiate request and the WebSocket handshake,
// which is what the console sends. The host has no query-string token path at all, deliberately:
// SignalR's access-token pattern puts the key in the URL, where it lands in access logs, proxies
// and crash reports.
if (app.Services.GetRequiredService<IOptions<TrafficOptions>>().Value.Enabled)
{
    app.MapHub<TrafficHub>(TrafficHub.Path)
        .RequireAuthorization(HostPolicies.Review);
}

app.Run();

return 0;

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the real host in tests.</summary>
public partial class Program;
