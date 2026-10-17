using Microsoft.Extensions.Logging.Abstractions;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The approved authenticated-account sink: one per connection, updates the existing live row, and
/// never lets a telemetry failure affect the relay.
/// </summary>
public sealed class ProxySessionContextSinkTests
{
    [Fact]
    public async Task The_sink_attributes_the_account_to_the_ambient_session()
    {
        var authenticator = new RecordingAuthenticator();
        var sink = new ProxySessionContextSink(authenticator, NullLogger.Instance);

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_42", "acme", ProxyListenerProtocol.Imap);
        try
        {
            await sink.OnAuthenticatedAsync(
                "pa_account", new DateTimeOffset(2026, 10, 17, 21, 0, 0, TimeSpan.Zero), CancellationToken.None);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }

        Assert.Equal(("acme", "ps_42", "pa_account"), authenticator.Last);
    }

    [Fact]
    public async Task A_failing_write_is_swallowed_so_the_session_continues()
    {
        var sink = new ProxySessionContextSink(new ThrowingAuthenticator(), NullLogger.Instance);

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_42", "acme", ProxyListenerProtocol.Imap);
        try
        {
            await sink.OnAuthenticatedAsync("pa_account", DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }
    }

    private sealed class RecordingAuthenticator : IProxySessionAuthenticator
    {
        public (string Tenant, string Session, string Account)? Last { get; private set; }

        public ValueTask MarkAuthenticatedAsync(
            string tenantId,
            string sessionId,
            string accountId,
            DateTimeOffset authenticatedAt,
            CancellationToken cancellationToken)
        {
            Last = (tenantId, sessionId, accountId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuthenticator : IProxySessionAuthenticator
    {
        public ValueTask MarkAuthenticatedAsync(
            string tenantId,
            string sessionId,
            string accountId,
            DateTimeOffset authenticatedAt,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("telemetry storage is down");
    }
}
