using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Traffic;

namespace StyloMail.Host.Tests;

/// <summary>
/// The live-traffic seam at its edges: what a deployment that has not enabled it gets, and what a
/// console can tell about a Host it is talking to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The feature is off unless a deployment turns it on.</b> Everything here is about that being
/// true of the artefact rather than of the intent: an unconfigured host resolves the no-op port,
/// offers no hub route at all, and its mail path is exactly what it was before this seam existed.
/// </para>
/// <para>
/// <b>An absent route and an unreachable one are different answers, and a console must be able to
/// tell them apart.</b> The whole failure this seam is written against is a feed that silently
/// freezes: a console that reconnects forever against a deployment that will never offer the hub
/// looks identical to one whose hub is briefly down, and the operator is told nothing either way.
/// A 404 on the negotiate route says "this Host has no live feed"; a 401 or a failed connection says
/// "this Host has one and you are not live on it". Those are different sentences on screen, so they
/// are different responses on the wire.
/// </para>
/// </remarks>
public sealed class TrafficSeamTests
{
    [Fact]
    public void An_unconfigured_deployment_resolves_the_no_op_port()
    {
        // The default implementation is the flag-off path, so "not configured" and "emission goes
        // nowhere" are the same fact rather than two that can drift apart.
        using var host = new TestHost();

        var events = host.Services.GetRequiredService<ITrafficEvents>();

        Assert.IsType<NullTrafficEvents>(events);
    }

    [Fact]
    public void The_no_op_port_accepts_every_change_and_does_nothing_with_it()
    {
        // Nothing is asserted about delivery, because there is none; what is asserted is that
        // publishing is not a failure. A no-op that threw would be a flag-off deployment whose mail
        // path depended on the hub, which is the one thing the port exists to make impossible.
        var events = NullTrafficEvents.Instance;

        events.Publish(TrafficEvent.DecisionRecorded("acme", "asm_1", DateTimeOffset.UnixEpoch));
        events.Publish(TrafficEvent.MessageStateChanged("acme", "q_1", DateTimeOffset.UnixEpoch));
        events.Publish(TrafficEvent.SenderControlChanged("acme", "ops@acme", DateTimeOffset.UnixEpoch));
        events.Publish(TrafficEvent.ReadinessChanged(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task An_unconfigured_deployment_answers_the_hub_route_with_404()
    {
        // 404 rather than 401 or 503, and the same reasoning as the Cloudflare intake's route: a
        // route that exists and always refuses invites a configuration change to "fix" it. A
        // deployment that never enabled live traffic has no such route, and says so.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await client.PostAsync("/v1/traffic/negotiate", content: null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_enabled_deployment_negotiates_a_connection_for_a_reviewer()
    {
        using var host = WithTraffic();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // A POST, which is what SignalR's negotiate is: the pair of tests here differ in the
        // deployment's configuration and in nothing else, so a 404 and a 200 are answers about the
        // deployment rather than about the method that asked.
        var response = await client.PostAsync("/v1/traffic/negotiate", content: null);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void A_change_about_a_tenant_is_addressed_to_that_tenants_group_alone()
    {
        // Tenant isolation is the group name, and the group name is the tenant, so a change cannot
        // be delivered to a tenant it does not belong to without the group name having been wrong.
        var hub = new RecordingHubContext();
        var events = new SignalRTrafficEvents(hub);

        events.Publish(TrafficEvent.DecisionRecorded("acme", "asm_1", DateTimeOffset.UnixEpoch));

        var send = Assert.Single(hub.Sends);
        Assert.Equal("group:acme", send.Audience);
    }

    [Fact]
    public void A_change_about_the_host_reaches_every_connection()
    {
        // Readiness belongs to the Host rather than to a tenant, and it is already served without
        // credentials at /health/ready. Scoping it to a tenant would mean a console watching a
        // failing Host was told nothing about it, which is the opposite of what this seam is for.
        var hub = new RecordingHubContext();
        var events = new SignalRTrafficEvents(hub);

        events.Publish(TrafficEvent.ReadinessChanged(DateTimeOffset.UnixEpoch));

        var send = Assert.Single(hub.Sends);
        Assert.Equal("all", send.Audience);
    }

    [Fact]
    public void What_goes_on_the_wire_is_a_hint_and_never_carries_state()
    {
        // The console re-reads the row over HTTP; this says only which row. A payload that carried
        // the verdict itself would make a dropped, duplicated or reordered event a permanently
        // wrong screen, so the notice names an identifier and an instant and nothing else.
        var hub = new RecordingHubContext();
        var events = new SignalRTrafficEvents(hub);

        events.Publish(TrafficEvent.DecisionRecorded(
            "acme",
            "asm_1",
            DateTimeOffset.UnixEpoch));

        var notice = Assert.IsType<TrafficNotice>(Assert.Single(hub.Sends).Args[0]);
        Assert.Equal(TrafficEventKind.DecisionRecorded, notice.Kind);
        Assert.Equal("asm_1", notice.SubjectId);
        Assert.Equal(DateTimeOffset.UnixEpoch, notice.OccurredAt);

        // Named rather than inferred from the absence of a field: the tenant is the routing key,
        // and a notice that carried it would be publishing the one thing the group already says.
        Assert.Equal(
            ["Kind", "OccurredAt", "SubjectId"],
            typeof(TrafficNotice).GetProperties()
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hub_that_fails_cannot_escape_the_port(bool faultAfterTheSend)
    {
        // The seam's whole body, and the reason it is a port rather than a hub context handed
        // around: every real implementation wraps what it does, so the failure above never reaches
        // an assessment or a delivery.
        //
        // Awaited rather than fired and forgotten, so that this is a test of the wrapper rather
        // than of the discard: a discarded task absorbs any exception, which would make a missing
        // catch look perfectly safe here while leaving the same fault unobserved underneath.
        //
        // Both failure shapes, and all four kinds on each: a synchronous throw happens before the
        // awaited send, a faulted task after it, and a wrapper that covered one path and not the
        // other would satisfy a test that only used the other.
        Microsoft.AspNetCore.SignalR.IHubContext<TrafficHub> hub = faultAfterTheSend
            ? new FaultingHubContext()
            : new ThrowingHubContext();

        var events = new SignalRTrafficEvents(hub);

        await events.PublishAsync(TrafficEvent.DecisionRecorded("acme", "asm_1", DateTimeOffset.UnixEpoch));
        await events.PublishAsync(TrafficEvent.MessageStateChanged("acme", "q_1", DateTimeOffset.UnixEpoch));
        await events.PublishAsync(TrafficEvent.SenderControlChanged("acme", "ops@acme", DateTimeOffset.UnixEpoch));
        await events.PublishAsync(TrafficEvent.ReadinessChanged(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task Even_a_null_change_cannot_escape_the_port()
    {
        // The class says this cannot fail for any input, so a null argument is inside that claim
        // rather than outside it. The guard every other component in this codebase would start with
        // is the guard that must not be here, because it would throw into an assessment, and the
        // test is what stops the next reader from adding it back for consistency.
        var events = new SignalRTrafficEvents(new RecordingHubContext());

        await events.PublishAsync(null!);
        events.Publish(null!);
    }

    [Fact]
    public async Task A_change_that_cannot_be_addressed_is_dropped_rather_than_broadcast()
    {
        // A tenant-scoped change with no tenant has exactly two possible destinations, and both are
        // wrong: sending it to nobody, or sending it to everybody. The second is a cross-tenant
        // disclosure, so an unaddressable change is dropped, and dropping it costs nothing, because
        // a notice is a hint and the console's own reads are what it renders from.
        //
        // This is the case a caller reaches by accident, an event built directly rather than
        // through a factory, and the answer is that the seam fails closed rather than wide.
        var hub = new RecordingHubContext();
        var events = new SignalRTrafficEvents(hub);

        await events.PublishAsync(new TrafficEvent
        {
            Kind = TrafficEventKind.DecisionRecorded,
            SubjectId = "asm_with_no_tenant",
            OccurredAt = DateTimeOffset.UnixEpoch,
        });

        Assert.Empty(hub.Sends);
    }

    [Fact]
    public async Task An_event_built_with_nothing_in_it_is_dropped_rather_than_thrown()
    {
        // The port's contract is that it cannot fail, and that has to hold for a caller's mistake
        // as much as for an outage. A guard that refused this would throw into an assessment, which
        // is the one thing this seam may never do, so the guard is a drop instead.
        var hub = new RecordingHubContext();
        var events = new SignalRTrafficEvents(hub);

        await events.PublishAsync(new TrafficEvent
        {
            Kind = TrafficEventKind.MessageStateChanged,
            OccurredAt = DateTimeOffset.UnixEpoch,
        });

        Assert.Empty(hub.Sends);
    }

    [Fact]
    public void The_fire_and_forget_form_is_the_awaited_one()
    {
        // Both entry points, so that the discard above cannot quietly become a second, unguarded
        // path into the transport.
        var events = new SignalRTrafficEvents(new ThrowingHubContext());

        events.Publish(TrafficEvent.ReadinessChanged(DateTimeOffset.UnixEpoch));
    }

    /// <summary>A deployment that has switched live traffic on.</summary>
    internal static TestHost WithTraffic()
    {
        var host = new TestHost();
        host.Configure("StyloMail:Traffic:Enabled", "true");

        return host;
    }
}
