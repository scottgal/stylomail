using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Submission;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

public sealed class ProxyTlsAdmissionTests
{
    [Theory]
    [InlineData(false, ProxyListenerProtocol.Imap)]
    [InlineData(true, ProxyListenerProtocol.Imap)]
    [InlineData(false, ProxyListenerProtocol.Pop3)]
    [InlineData(true, ProxyListenerProtocol.Pop3)]
    public async Task Silent_tcp_client_is_closed_by_eager_handshake_deadline(bool library, ProxyListenerProtocol protocol)
    {
        await using var fixture = await Listener.Create(library, protocol, 1, TimeSpan.FromMilliseconds(500));
        await fixture.ProveSecureGreeting();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port);
        await Listener.WaitFor(() => fixture.Clients == 1);
        Assert.True(await ClosedWithin(client, TimeSpan.FromMilliseconds(1500)), "No-ClientHello socket outlived handshake deadline plus tolerance.");
        await Listener.WaitFor(() => fixture.Clients == 0 && fixture.Sessions == 0 && fixture.Limiter.ActiveSessions == 0);
        // The expired connection must release admission so another real TLS session can start.
        await fixture.ProveSecureGreeting();
    }

    [Theory]
    [InlineData(false, ProxyListenerProtocol.Imap)]
    [InlineData(true, ProxyListenerProtocol.Imap)]
    [InlineData(false, ProxyListenerProtocol.Pop3)]
    [InlineData(true, ProxyListenerProtocol.Pop3)]
    public async Task Handshake_holds_the_only_slot_and_excess_socket_is_not_queued(bool library, ProxyListenerProtocol protocol)
    {
        await using var fixture = await Listener.Create(library, protocol, 1, TimeSpan.FromSeconds(3));
        await fixture.ProveSecureGreeting(); // cap1 proves protocol admission is not acquired twice.
        using (var first = new TcpClient(AddressFamily.InterNetwork))
        {
            await first.ConnectAsync(IPAddress.Loopback, fixture.Port);
            await Listener.WaitFor(() => fixture.Clients == 1);
            Assert.Equal(1, fixture.Limiter.ActiveSessions);
            using var excess = new TcpClient(AddressFamily.InterNetwork);
            await excess.ConnectAsync(IPAddress.Loopback, fixture.Port);
            Assert.True(await ClosedWithin(excess, TimeSpan.FromMilliseconds(700)), "Excess accepted socket was queued instead of refused.");
            Assert.Equal(1, fixture.Clients);
            Assert.Equal(1, fixture.Sessions);
            Assert.Equal(1, fixture.Limiter.ActiveSessions);
        }
        await Listener.WaitFor(() => fixture.Clients == 0 && fixture.Sessions == 0 && fixture.Limiter.ActiveSessions == 0);
        await fixture.ProveSecureGreeting();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_ephemeral_port_is_preserved_after_stop_and_disposal(bool library)
    {
        await using var fixture = await Listener.Create(library, ProxyListenerProtocol.Imap, 1, TimeSpan.FromSeconds(3));
        await fixture.ProveSecureGreeting();
        var port = fixture.Port;
        Assert.InRange(port, 1, 65535);
        Assert.Equal(IPAddress.Loopback, fixture.Tcp.Endpoint.Address);
        Assert.Equal(0, fixture.Tcp.Endpoint.Port);
        await fixture.Stop();
        Assert.Equal(port, fixture.Tcp.BoundPort);
        if (library)
            Assert.Equal(port, Assert.Single(fixture.Events.Listeners, e => e.Kind == ProxyListenerEventKind.Stopped).BoundPort);
        else
            Assert.Equal(port, fixture.Handle!.BoundPort);
        await fixture.DisposeAsync();
        Assert.Equal(port, fixture.Tcp.BoundPort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Smtp_starttls_keeps_single_admission_and_secure_capabilities(bool library)
    {
        await using var fixture = await Listener.Create(library, ProxyListenerProtocol.Smtp, 1, TimeSpan.FromSeconds(3));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port);
        using (var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true))
        using (var writer = new StreamWriter(client.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
        {
            Assert.StartsWith("220", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, fixture.Limiter.ActiveSessions);
            await writer.WriteLineAsync("EHLO fixture");
            Assert.StartsWith("250-", await reader.ReadLineAsync());
            Assert.Equal("250 STARTTLS", await reader.ReadLineAsync());
            await writer.WriteLineAsync("STARTTLS");
            Assert.StartsWith("220", await reader.ReadLineAsync());
        }
        await using (var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, deadline.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("EHLO fixture");
            Assert.StartsWith("250-", await reader.ReadLineAsync(deadline.Token));
            Assert.Equal("250 AUTH PLAIN LOGIN", await reader.ReadLineAsync(deadline.Token));
            Assert.Equal(1, fixture.Limiter.ActiveSessions);
            await writer.WriteLineAsync("QUIT");
            Assert.StartsWith("221", await reader.ReadLineAsync(deadline.Token));
        }
        await Listener.WaitFor(() => fixture.Clients == 0 && fixture.Sessions == 0 && fixture.Limiter.ActiveSessions == 0);
    }

    private static async Task<bool> ClosedWithin(TcpClient client, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { return await client.GetStream().ReadAsync(new byte[64], deadline.Token) == 0; }
        catch (IOException) { return true; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly TestHost _host;
        private readonly ProxyListenerProtocol _protocol;
        private readonly ProxyListenerHostedService? _library;
        private readonly X509Certificate2? _runnerCertificate;
        private int _disposed;
        public IProxyListenerHandle? Handle { get; }
        public TcpAccessProxyListener Tcp { get; }
        public int Port { get; }
        public SessionLimiter Limiter { get; }
        public Events Events { get; }
        public int Clients => Count("_clients");
        public int Sessions => Count("_sessions");
        private int Count(string field)
        {
            var dictionary = typeof(TcpAccessProxyListener).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Tcp)!;
            return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
        }
        private Listener(TestHost host, ProxyListenerProtocol protocol, ProxyListenerHostedService? library, IProxyListenerHandle? handle,
            Events events, X509Certificate2? runnerCertificate = null)
        {
            _host = host; _protocol = protocol; _library = library; Handle = handle; Events = events;
            _runnerCertificate = runnerCertificate;
            Limiter = host.Services.GetRequiredService<SessionLimiter>();
            object wrapper = (object?)library ?? handle!;
            Tcp = (TcpAccessProxyListener)wrapper.GetType().GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrapper)!;
            Port = Tcp.BoundPort;
        }
        public static async Task<Listener> Create(bool library, ProxyListenerProtocol protocol, int cap, TimeSpan timeout)
        {
            var bounds = new AccessProxyBounds { MaxConcurrentSessions = cap, MaxSessionsPerAccount = cap, AuthenticationTimeout = timeout };
            var events = new Events();
            X509Certificate2 certificate;
            using (var key = RSA.Create(2048))
                certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            var host = new TestHost().Configure("StyloMail:Proxy:Enabled", "true").Override(services =>
            {
                services.RemoveAll<AccessProxyBounds>(); services.AddSingleton(bounds);
                services.RemoveAll<IProxyListenerEventSink>(); services.AddSingleton<IProxyListenerEventSink>(events);
                services.RemoveAll<IProxySecretSource>(); services.AddSingleton<IProxySecretSource>(new EphemeralSecrets());
                services.RemoveAll<IProxyCertificateResolver>(); services.AddSingleton<IProxyCertificateResolver>(new CertificateResolver(certificate));
            });
            try
            {
                var services = host.Services;
                var options = new HostProxyListenerOptions
                {
                    ListenerId = "fixture", TenantId = TestPrincipals.AcmeTenant, BindAddress = "127.0.0.1", Port = 0,
                    Protocol = protocol, TlsPolicy = ProxyTlsPolicy.Required, CertificateReference = "ephemeral-certificate", Enabled = true,
                };
                if (!library)
                {
                    var handle = services.GetRequiredService<IProxyListenerHandleFactory>().Create(options)!;
                    await handle.StartAsync(CancellationToken.None);
                    return new Listener(host, protocol, null, handle, events, certificate);
                }
                var runnerOptions = new ProxyListenerOptions
                {
                    TenantId = options.TenantId, BindAddress = IPAddress.Loopback, Port = 0, Protocol = protocol,
                    TlsPolicy = ProxyTlsPolicy.Required, CertificateReference = "ephemeral-certificate", Enabled = true,
                };
                var dependencies = new ProxySessionRunnerDependencies
                {
                    Accounts = services.GetRequiredService<IStyloMailAccountStore>(),
                    PasswordHasher = services.GetRequiredService<IStyloMailPasswordHasher>(),
                    CredentialStore = services.GetRequiredService<IBackendCredentialStore>(),
                    CredentialResolver = services.GetRequiredService<BackendCredentialResolver>(),
                    BackendConnectors = services.GetRequiredService<IProxyBackendConnectorFactory>(),
                    Limiter = services.GetRequiredService<SessionLimiter>(), Bounds = bounds, TimeProvider = TimeProvider.System,
                    ContextSink = new ContextSink(), WarningSink = services.GetRequiredService<IProxyWarningEventSink>(),
                    WarningAnalyzer = services.GetRequiredService<IProxyWarningAnalyzer>(),
                    SubmissionAssessment = services.GetRequiredService<IProxySubmissionAssessment>(),
                    SubmissionStager = services.GetRequiredService<IEphemeralSubmissionStager>(),
                    Tls = new TlsServerOptions { ServerCertificate = certificate },
                };
                // Keep these controls runnable against the pre-repair five-argument API as well.
                // Both branches construct the actual production wrapper and protocol runner.
                var constructor = typeof(ProxyListenerHostedService).GetConstructors().Single();
                object[] arguments = [runnerOptions, services.GetRequiredService<IProxyCertificateResolver>(),
                    events, ProxySessionRunnerFactory.Create(runnerOptions, dependencies), TimeProvider.System];
                if (constructor.GetParameters().Length == 7) arguments = [.. arguments, bounds, dependencies.Limiter];
                var service = (ProxyListenerHostedService)constructor.Invoke(arguments);
                await service.StartAsync(CancellationToken.None);
                return new Listener(host, protocol, service, null, events, certificate);
            }
            catch { host.Dispose(); certificate.Dispose(); throw; }
        }
        public async Task ProveSecureGreeting()
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                await client.ConnectAsync(IPAddress.Loopback, Port);
                await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, deadline.Token);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                var greeting = await reader.ReadLineAsync(deadline.Token);
                Assert.StartsWith(_protocol == ProxyListenerProtocol.Pop3 ? "+OK" : "* OK", greeting);
                Assert.Equal(1, Limiter.ActiveSessions);
            }
            await Listener.WaitFor(() => Clients == 0 && Sessions == 0 && Limiter.ActiveSessions == 0);
        }
        public static async Task WaitFor(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!condition()) await Task.Delay(10, deadline.Token);
        }
        public Task Stop() => _library?.StopAsync(CancellationToken.None) ?? Handle!.StopAsync(CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (_library is not null) await _library.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (Handle is not null) await Handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { _host.Dispose(); _runnerCertificate?.Dispose(); }
        }
    }
    private sealed class ContextSink : IProxySessionContextSink
    {
        public ValueTask OnAuthenticatedAsync(string accountId, DateTimeOffset authenticatedAt, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class CertificateResolver(X509Certificate2 certificate) : IProxyCertificateResolver
    {
        public ValueTask<X509Certificate2?> ResolveAsync(string reference, CancellationToken cancellationToken)
            => ValueTask.FromResult<X509Certificate2?>(certificate);
    }
    private sealed class Events : IProxyListenerEventSink
    {
        public List<ProxyListenerEvent> Listeners { get; } = [];
        public ValueTask PublishAsync(ProxyListenerEvent e, CancellationToken token) { Listeners.Add(e); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(ProxySessionEvent e, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class EphemeralSecrets : IProxySecretSource
    {
        private readonly byte[] _login = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _protection = RandomNumberGenerator.GetBytes(32);
        public byte[] GetLoginKey() => _login.ToArray();
        public byte[] GetProtectionKey() => _protection.ToArray();
    }
}
