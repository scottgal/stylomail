using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Host.Traffic;

namespace StyloMail.Host.Tests;

/// <summary>
/// The live feed as a console actually reaches it: a real client, over a real connection, through
/// the host's own composition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events are a hint, never state.</b> What arrives says which row moved; the console re-reads
/// the row over HTTP. So what is asserted here is addressing, arrival and shape, never a verdict:
/// a payload carrying the decision would be the defect this seam is designed around, not a
/// convenience.
/// </para>
/// <para>
/// <b>The console must be able to tell live from stale.</b> The two things a console needs for that
/// are both here: a connection that either establishes or does not, and no route at all on a
/// deployment that has not enabled the feature. A feed that silently freezes looks exactly like a
/// quiet system, which is why "is there a feed" and "am I on it" are different questions with
/// different answers.
/// </para>
/// </remarks>
public sealed class TrafficHubTests
{
    [Fact]
    public async Task A_connection_without_a_key_is_refused()
    {
        using var host = TrafficSeamTests.WithTraffic();
        using var anonymous = host.Anonymous();

        var response = await anonymous.PostAsync("/v1/traffic/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_in_the_url_is_not_a_credential_this_host_reads()
    {
        // SignalR's access-token pattern puts the key in the query string, where it lands in access
        // logs, proxies and crash reports. The console sends a header instead, and the host reads
        // nothing from the URL: this asserts that rather than trusting the client to remember.
        using var host = TrafficSeamTests.WithTraffic();
        using var viaUrl = host.Anonymous();

        var response = await viaUrl.PostAsync(
            $"/v1/traffic/negotiate?access_token={TestPrincipals.AcmeReviewerKey}",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_caller_without_the_review_privilege_cannot_subscribe()
    {
        // Subscribing launches no capability: the feed tells a caller that a row it may not read has
        // moved. It is nonetheless a read of a tenant's traffic, so it takes the same privilege the
        // rest of that tenant's read surface takes, and a sending principal holds none of it.
        using var host = TrafficSeamTests.WithTraffic();
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);

        var response = await sender.PostAsync("/v1/traffic/negotiate", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_console_hears_its_own_tenants_change_and_another_tenants_not_at_all()
    {
        // The isolation rule, and the reason the group is derived from the authenticated principal:
        // nothing a client sends chooses its audience, so there is no request that could widen it.
        using var host = TrafficSeamTests.WithTraffic();

        await using var acme = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);
        await using var globex = await host.SubscribeTrafficAsync(TestPrincipals.GlobexReviewerKey);

        var events = host.Services.GetRequiredService<ITrafficEvents>();

        // Membership is proven before anything negative is asserted. Without these two exchanges a
        // client that had silently failed to join anything would satisfy "heard nothing" perfectly.
        events.Publish(TrafficEvent.DecisionRecorded(
            TestPrincipals.AcmeTenant, "asm_acme", DateTimeOffset.UnixEpoch));
        await acme.NextAsync();

        events.Publish(TrafficEvent.DecisionRecorded(
            TestPrincipals.GlobexTenant, "asm_globex", DateTimeOffset.UnixEpoch));
        await globex.NextAsync();

        events.Publish(TrafficEvent.DecisionRecorded(
            TestPrincipals.AcmeTenant, "asm_acme_second", DateTimeOffset.UnixEpoch));

        await acme.NextAsync();

        var heardByGlobex = await globex.ReceiveAndSettleAsync();

        // Everything globex has ever heard, in order: its own tenant's change and nothing acme
        // produced. Written as the whole history rather than as "the last one was not acme's",
        // because that is the claim: a subscriber's entire feed is its own tenant's traffic. The
        // acme change published after both memberships were established is the one that would be
        // sitting at the end of this list if the audience were wrong.
        Assert.Collection(
            heardByGlobex,
            only => Assert.Equal("asm_globex", only.GetProperty("subjectId").GetString()));
    }

    [Fact]
    public async Task A_notice_names_its_kind_instead_of_numbering_it()
    {
        // The console switches on the kind, so a numeric payload would make every live screen depend
        // on the order the enum members happen to be declared in, and reordering them would be a
        // silent change of meaning. Asserted on the bytes that arrived, not on a deserialised
        // object, because a client that happens to share this assembly would hide exactly this.
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.AcmeReviewerKey);

        host.Services.GetRequiredService<ITrafficEvents>().Publish(TrafficEvent.MessageStateChanged(
            TestPrincipals.AcmeTenant, "q_1", DateTimeOffset.UnixEpoch));

        var notice = await console.NextAsync();

        Assert.Equal("MessageStateChanged", notice.GetProperty("kind").GetString());
        Assert.Equal("q_1", notice.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task A_console_subscribes_over_the_transport_it_will_actually_use()
    {
        // The console connects over a WebSocket, and that is the transport rule 3 is about: the key
        // has to survive the upgrade handshake as a header, because the only alternative SignalR
        // offers is the query string, where it would land in access logs, proxies and crash
        // reports. Long polling reuses the negotiate request for everything, so a suite that only
        // ever used it would be green over a WebSocket path that refused every console.
        using var host = TrafficSeamTests.WithTraffic();

        await using var console = await host.SubscribeTrafficAsync(
            TestPrincipals.AcmeReviewerKey,
            Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets);

        host.Services.GetRequiredService<ITrafficEvents>().Publish(TrafficEvent.DecisionRecorded(
            TestPrincipals.AcmeTenant, "asm_over_the_upgrade", DateTimeOffset.UnixEpoch));

        var notice = await console.NextAsync();

        Assert.Equal("asm_over_the_upgrade", notice.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task A_console_hears_a_host_wide_change_without_being_in_a_host_group()
    {
        // Readiness is a fact about the host, so it reaches every authenticated connection. A
        // console watching a host that has stopped being able to accept mail is the case this
        // exists for, and it belongs to no tenant.
        using var host = TrafficSeamTests.WithTraffic();
        await using var console = await host.SubscribeTrafficAsync(TestPrincipals.GlobexReviewerKey);

        host.Services.GetRequiredService<ITrafficEvents>()
            .Publish(TrafficEvent.ReadinessChanged(DateTimeOffset.UnixEpoch));

        var notice = await console.NextAsync();

        Assert.Equal("ReadinessChanged", notice.GetProperty("kind").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, notice.GetProperty("subjectId").ValueKind);
    }
}
