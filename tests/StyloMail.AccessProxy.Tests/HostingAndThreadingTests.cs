using System.Net;
using System.Security.Cryptography.X509Certificates;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Tests.Support;
using StyloMail.AccessProxy.Threading;

namespace StyloMail.AccessProxy.Tests;

public sealed class HostingAndThreadingTests
{
    [Fact]
    public void Imap_header_extractor_stops_before_body_sentinel()
    {
        var extractor = new ThreadingHeaderExtractor(new Tokens());
        var headerAndBody = System.Text.Encoding.UTF8.GetBytes(
            "Message-ID: <m1>\r\nReferences: <root>\r\nSubject: ignored\r\n\r\nUNIQUE-BODY-SENTINEL");
        var result = extractor.Extract("tenant", "account", headerAndBody, DateTimeOffset.UnixEpoch);
        Assert.Equal("token:<m1>", result.MessageToken);
        Assert.Equal("token:<root>", result.ThreadToken);
        Assert.DoesNotContain("UNIQUE-BODY-SENTINEL", result.MessageToken + result.ThreadToken);
    }

    [Fact]
    public async Task Smtp_plaintext_listener_policy_fails_before_binding()
    {
        var service = new ProxyListenerHostedService(new ProxyListenerOptions
        {
            TenantId = "tenant", BindAddress = IPAddress.Loopback, Port = 0,
            Protocol = ProxyListenerProtocol.Smtp, TlsPolicy = ProxyTlsPolicy.LoopbackHarnessPlaintext, Enabled = true,
        }, new Certificates(), new Events(), (_, _, _) => Task.FromResult(SessionOutcome.ClientDisconnected), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Smtp_session_factory_requires_tls_at_construction()
    {
        var h = new ProxyHarness();
        var dependencies = new ProxySessionRunnerDependencies
        {
            Accounts = h.AccountStore, PasswordHasher = h.PasswordHasher, CredentialStore = h.CredentialStore,
            CredentialResolver = h.Resolver, BackendConnectors = new Connectors(), Limiter = h.Limiter,
            Bounds = h.Bounds, TimeProvider = h.Clock,
        };
        var options = new ProxyListenerOptions { TenantId = ProxyHarness.TenantId, BindAddress = IPAddress.Loopback,
            Port = 0, Protocol = ProxyListenerProtocol.Smtp, TlsPolicy = ProxyTlsPolicy.Required, Enabled = true };
        Assert.Throws<InvalidOperationException>(() => ProxySessionRunnerFactory.Create(options, dependencies));
    }

    private sealed class Tokens : IThreadTokenDeriver { public string Derive(string tenantId, string value) => "token:" + value; }
    private sealed class Certificates : IProxyCertificateResolver { public ValueTask<X509Certificate2?> ResolveAsync(string r, CancellationToken c) => ValueTask.FromResult<X509Certificate2?>(null); }
    private sealed class Events : IProxyListenerEventSink { public ValueTask PublishAsync(ProxyListenerEvent e, CancellationToken c) => ValueTask.CompletedTask; public ValueTask PublishAsync(ProxySessionEvent e, CancellationToken c) => ValueTask.CompletedTask; }
    private sealed class Connectors : IProxyBackendConnectorFactory { public StyloMail.AccessProxy.Backends.IBackendConnector Create(ProxyListenerProtocol p) => throw new NotSupportedException(); }
}
