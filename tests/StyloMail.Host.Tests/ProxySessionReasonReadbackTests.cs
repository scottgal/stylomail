using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Contracts;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

public sealed class ProxySessionReasonReadbackTests
{
    [Fact]
    public async Task Stored_bound_reasons_roundtrip_verbatim_and_sql_null_stays_null_without_changing_live_counts()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sink = host.Services.GetRequiredService<IProxyListenerEventSink>();
        var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await sink.PublishAsync(new ProxyListenerEvent
        {
            Kind = ProxyListenerEventKind.Started, TenantId = TestPrincipals.AcmeTenant,
            Protocol = ProxyListenerProtocol.Imap, Timestamp = now, BoundPort = 143,
        }, CancellationToken.None);
        ProxyBoundReason[] reasons = [ProxyBoundReason.AuthenticationTimeout, ProxyBoundReason.IdleTimeout,
            ProxyBoundReason.MaximumDuration, ProxyBoundReason.SubmissionLimit, ProxyBoundReason.None];
        foreach (var reason in reasons)
        {
            await sink.PublishAsync(new ProxySessionEvent
            {
                Kind = ProxySessionEventKind.Opened, SessionId = "s-" + reason, TenantId = TestPrincipals.AcmeTenant,
                Protocol = ProxyListenerProtocol.Imap, Direction = ProxySessionDirection.ClientToProxy,
                Timestamp = now, BoundReason = reason,
            }, CancellationToken.None);
        }
        // Current production DDL is NOT NULL. Exercise actual SQL NULL readback with a nullable
        // table copy in this disposable test database, without widening production schema scope.
        using (var connection = host.Services.GetRequiredService<HostDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE proxy_session_event RENAME TO reason_fixture; "
                + "CREATE TABLE proxy_session_event AS SELECT * FROM reason_fixture; "
                + "DROP TABLE reason_fixture; "
                + "UPDATE proxy_session_event SET bound_reason=NULL WHERE session_id='s-None';";
            command.ExecuteNonQuery();
        }
        var before = await reviewer.GetFromJsonAsync<ProxyOperationsResponse>("/v1/proxy/operations?window=last_24_hours");
        Assert.NotNull(before);
        Assert.Equal(5L, before.Counters.LiveSessions);
        var response = await reviewer.GetFromJsonAsync<ProxySessionsResponse>("/v1/proxy/sessions?scope=live");
        Assert.NotNull(response);
        Assert.Equal(5, response.Sessions.Count);
        foreach (var reason in reasons.Take(4))
            Assert.Equal(reason.ToString(), Assert.Single(response.Sessions, row => row.SessionId == "s-" + reason).BoundReason);
        Assert.Null(Assert.Single(response.Sessions, row => row.SessionId == "s-None").BoundReason);
        var after = await reviewer.GetFromJsonAsync<ProxyOperationsResponse>("/v1/proxy/operations?window=last_24_hours");
        Assert.NotNull(after);
        Assert.Equal(before.Counters.LiveSessions, after.Counters.LiveSessions);
    }
}
