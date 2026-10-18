using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
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
            Bounds = h.Bounds, TimeProvider = h.Clock, ContextSink = new ContextSink(), WarningSink = new WarningSink(), WarningAnalyzer = new WarningAnalyzer(),
        };
        var options = new ProxyListenerOptions { TenantId = ProxyHarness.TenantId, BindAddress = IPAddress.Loopback,
            Port = 0, Protocol = ProxyListenerProtocol.Smtp, TlsPolicy = ProxyTlsPolicy.Required, Enabled = true };
        Assert.Throws<InvalidOperationException>(() => ProxySessionRunnerFactory.Create(options, dependencies));
    }

    [Fact]
    public async Task Tls_required_retrieval_listener_never_speaks_plaintext()
    {
        using var certificate = CreateCertificate();
        var sessions = 0;
        await using var service = new ProxyListenerHostedService(new ProxyListenerOptions
        {
            TenantId = "tenant", BindAddress = IPAddress.Loopback, Port = 0,
            Protocol = ProxyListenerProtocol.Imap, TlsPolicy = ProxyTlsPolicy.Required,
            CertificateReference = "test-certificate", Enabled = true,
        }, new FixedCertificateResolver(certificate), new Events(), async (_, channel, _) =>
        {
            Interlocked.Increment(ref sessions);
            await channel.Output.WriteAsync("* OK secure retrieval\r\n"u8, CancellationToken.None);
            await channel.Output.FlushAsync(CancellationToken.None);
            return SessionOutcome.ClientDisconnected;
        }, TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        var port = service.BoundPort;

        try
        {
            using (var plaintext = new TcpClient())
            {
                await plaintext.ConnectAsync(IPAddress.Loopback, port);
                await plaintext.GetStream().WriteAsync("a1 LOGIN user password\r\n"u8);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try
                {
                    var read = await plaintext.GetStream().ReadAsync(new byte[64], timeout.Token);
                    Assert.Equal(0, read);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // The TLS listener must not emit an IMAP greeting over plaintext.
                }
            }

            Assert.Equal(0, Volatile.Read(ref sessions));
            using var tlsClient = new TcpClient();
            await tlsClient.ConnectAsync(IPAddress.Loopback, port);
            await using var tls = new SslStream(tlsClient.GetStream(), leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("localhost", CancellationToken.None);
            var greeting = new byte[32];
            var greetingLength = await tls.ReadAsync(greeting, CancellationToken.None);
            Assert.StartsWith("* OK", System.Text.Encoding.UTF8.GetString(greeting, 0, greetingLength), StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref sessions));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class Tokens : IThreadTokenDeriver { public string Derive(string tenantId, string value) => "token:" + value; }
    private sealed class Certificates : IProxyCertificateResolver { public ValueTask<X509Certificate2?> ResolveAsync(string r, CancellationToken c) => ValueTask.FromResult<X509Certificate2?>(null); }
    private sealed class FixedCertificateResolver(X509Certificate2 certificate) : IProxyCertificateResolver { public ValueTask<X509Certificate2?> ResolveAsync(string r, CancellationToken c) => ValueTask.FromResult<X509Certificate2?>(certificate); }
    private sealed class Events : IProxyListenerEventSink { public ValueTask PublishAsync(ProxyListenerEvent e, CancellationToken c) => ValueTask.CompletedTask; public ValueTask PublishAsync(ProxySessionEvent e, CancellationToken c) => ValueTask.CompletedTask; }
    private sealed class ContextSink : IProxySessionContextSink { public ValueTask OnAuthenticatedAsync(string a, DateTimeOffset t, CancellationToken c) => ValueTask.CompletedTask; }
    private sealed class WarningSink : IProxyWarningEventSink { public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome o, CancellationToken c) => ValueTask.CompletedTask; public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent m, CancellationToken c) => ValueTask.CompletedTask; }
    private sealed class WarningAnalyzer : IProxyWarningAnalyzer { public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput i, CancellationToken c) => ValueTask.FromResult(new ProxyWarningAnalysisResult { MessageToken = "message", ThreadToken = "thread", Availability = ProxyWarningAvailability.NotProduced, AvailabilityReason = ProxyWarningAvailabilityReason.NoAnalyserConfigured, Findings = [] }); }
    private sealed class Connectors : IProxyBackendConnectorFactory { public StyloMail.AccessProxy.Backends.IBackendConnector Create(ProxyListenerProtocol p) => throw new NotSupportedException(); }
}
