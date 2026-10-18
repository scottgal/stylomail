using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Imap;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Smtp;
using StyloMail.AccessProxy.Submission;
using StyloMail.Host.Proxy;

namespace StyloMail.ProxyHarness.Reliability;

internal sealed class ProxyFixture : IAsyncDisposable
{
    public const int BackendDeadlineMilliseconds = 350;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "proxy-reliability-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretKeyRing _keys = InMemorySecretKeyRing.CreateRandom("ephemeral");
    private readonly X509Certificate2 _root;
    private readonly ProxyTrustAnchors _anchors;
    private readonly BackendFixture _imapBackend;
    private readonly BackendFixture _smtpBackend;
    private readonly TcpAccessProxyListener _imap;
    private readonly TcpAccessProxyListener _smtp;
    private readonly ConcurrentDictionary<string, int> _outcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _clients = new(StringComparer.Ordinal);
    private int _active;
    private int _peak;
    public X509Certificate2 Certificate { get; }
    public string ClientPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public const string Login = "fixture@example.test";
    public SessionLimiter Limiter { get; }
    public InMemoryEphemeralSubmissionStager Stager { get; } = new();
    public int ImapPort => _imap.BoundPort;
    public int SmtpPort => _smtp.BoundPort;
    public int[] Ports => [ImapPort, SmtpPort, _imapBackend.Port, _smtpBackend.Port];
    public int BackendSessions => _imapBackend.Active + _smtpBackend.Active;
    public int TrackedClients => Count(_imap, "_clients") + Count(_smtp, "_clients");
    public int TrackedSessions => Count(_imap, "_sessions") + Count(_smtp, "_sessions");
    public int PeakSessions => Volatile.Read(ref _peak);
    public int ResultCount => _outcomes.Values.Sum();
    public int RelayedCount => _outcomes.Where(pair => pair.Key.EndsWith(":Relayed", StringComparison.Ordinal)).Sum(pair => pair.Value);
    public object Outcomes => _outcomes.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);
    public bool HasSingleOutcome(string protocol, params string[] allowed)
        => ResultCount == 1 && allowed.Any(outcome => _outcomes.TryGetValue(protocol + ":" + outcome, out var count) && count == 1);
    public object ClientResults => _clients.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);
    public string[] Unnamed => _outcomes.Keys.Where(key => key.Contains("UNNAMED", StringComparison.Ordinal)).ToArray();
    public string[] BackendErrors => _imapBackend.Errors.Concat(_smtpBackend.Errors).ToArray();
    public void ClientResult(string result) => _clients.AddOrUpdate(result, 1, (_, count) => count + 1);

    private ProxyFixture(BackendMode mode, int concurrency)
    {
        Directory.CreateDirectory(_directory);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=loopback-reliability-root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        _root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var leaf = request.Create(_root, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(16));
        Certificate = leaf.CopyWithPrivateKey(serverKey);
        // Public certificate only. Private keys and generated credentials remain in memory.
        var caPath = Path.Combine(_directory, "root.cer");
        File.WriteAllBytes(caPath, _root.Export(X509ContentType.Cert));
        _imapBackend = new BackendFixture(Certificate, smtp: false, mode);
        _smtpBackend = new BackendFixture(Certificate, smtp: true, mode);
        _imapBackend.Start(); _smtpBackend.Start();

        var clock = TimeProvider.System;
        var bounds = new AccessProxyBounds
        {
            MaxConcurrentSessions = concurrency, MaxSessionsPerAccount = concurrency,
            AuthenticationTimeout = TimeSpan.FromSeconds(3), BackendAuthenticationTimeout = TimeSpan.FromMilliseconds(BackendDeadlineMilliseconds),
            BackendConnectTimeout = TimeSpan.FromMilliseconds(BackendDeadlineMilliseconds), IdleTimeout = TimeSpan.FromSeconds(3),
            MaxSessionDuration = TimeSpan.FromSeconds(15), MaxSubmissionBytes = 64 * 1024,
        };
        Limiter = new SessionLimiter(bounds);
        var hasher = new Pbkdf2StyloMailPasswordHasher(iterations: 1000);
        var accounts = new InMemoryStyloMailAccountStore();
        accounts.Add(new StyloMailAccount
        {
            TenantId = "fixture", AccountId = "account", BackendAccountId = "account", Login = Login,
            PasswordHash = hasher.Hash(System.Text.Encoding.UTF8.GetBytes(ClientPassword)),
            CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(), Disabled = false,
        });
        var protector = new AesGcmSecretProtector(_keys);
        var credentials = new InMemoryBackendCredentialStore(protector);
        using (var secret = SecretValue.FromBytes(RandomNumberGenerator.GetBytes(24)))
            credentials.StoreAsync(new BackendCredentialWrite
            {
                TenantId = "fixture", AccountId = "account", ProviderId = "fixture", BackendUsername = Login,
                Discriminator = AppPasswordCredentialProvider.DiscriminatorValue, Secret = secret, TimeProvider = clock,
            }, CancellationToken.None).GetAwaiter().GetResult();
        var resolver = new BackendCredentialResolver(credentials, protector,
            new BackendCredentialProviders([new AppPasswordCredentialProvider()]), clock);
        var options = Options.Create(new HostProxyOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = new HostProxyProviderOptions
                {
                    ImapHost = "127.0.0.1", ImapPort = _imapBackend.Port, ImapTls = true,
                    SmtpHost = "127.0.0.1", SmtpPort = _smtpBackend.Port, SmtpTls = true, CaCertificatePath = caPath,
                },
            },
        });
        _anchors = new ProxyTrustAnchors(options, clock);
        var connectors = new HostProxyBackendConnectorFactory(new HostProxyBackendTransport(options, credentials, _anchors, bounds), resolver, bounds);
        var tlsOptions = new TlsServerOptions { ServerCertificate = Certificate };
        _imap = new(new IPEndPoint(IPAddress.Loopback, 0), "fixture", async (tenant, channel, token) =>
            await Track("imap", async () =>
            {
                await using var tls = await TlsServerDuplexChannel.AuthenticateAsync(channel.Input, tlsOptions, channel.Description, token);
                return await new ImapAccessProxySession(tenant, accounts, hasher, resolver,
                    connectors.Create(ProxyListenerProtocol.Imap), Limiter, bounds, clock).RunAsync(tls, token);
            }));
        _smtp = new(new IPEndPoint(IPAddress.Loopback, 0), "fixture", async (tenant, channel, token) =>
            await Track("smtp", async () =>
            {
                await using var tls = new StartTlsServerDuplexChannel(channel.Input, tlsOptions, channel.Description);
                return await new SmtpAccessProxySession(tenant, accounts, hasher,
                    connectors.Create(ProxyListenerProtocol.Smtp), Limiter, bounds, clock,
                    new AllowAssessment(), Stager, warningSink: new WarningSink(), warningAnalyzer: new WarningAnalyzer()).RunAsync(tls, token);
            }));
    }
    public static async Task<ProxyFixture> Create(BackendMode mode, int concurrency)
    {
        var fixture = new ProxyFixture(mode, concurrency);
        try { await fixture._imap.StartAsync(CancellationToken.None); await fixture._smtp.StartAsync(CancellationToken.None); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private async Task<SessionOutcome> Track(string protocol, Func<Task<SessionOutcome>> action)
    {
        var active = Interlocked.Increment(ref _active);
        int observed;
        do { observed = Volatile.Read(ref _peak); if (active <= observed) break; }
        while (Interlocked.CompareExchange(ref _peak, active, observed) != observed);
        try
        {
            var outcome = await action();
            _outcomes.AddOrUpdate(protocol + ":" + outcome, 1, (_, count) => count + 1);
            return outcome;
        }
        catch (Exception e)
        {
            _outcomes.AddOrUpdate(protocol + ":UNNAMED:" + e.GetType().Name, 1, (_, count) => count + 1);
            throw;
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    public async Task Settle(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (TrackedClients != 0 || TrackedSessions != 0 || BackendSessions != 0 || Limiter.ActiveSessions != 0 || Stager.ActiveCount != 0)
            await Task.Delay(20, deadline.Token);
    }
    private static int Count(TcpAccessProxyListener listener, string field)
    {
        var dictionary = typeof(TcpAccessProxyListener).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
        return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
    }
    public async ValueTask DisposeAsync()
    {
        await _imap.DisposeAsync(); await _smtp.DisposeAsync();
        await _imapBackend.DisposeAsync(); await _smtpBackend.DisposeAsync();
        Limiter.Dispose();
        _anchors.Get("fixture")?.Dispose(); _root.Dispose(); Certificate.Dispose();
        if (_keys.GetKey("ephemeral") is { } key) CryptographicOperations.ZeroMemory(key);
        Directory.Delete(_directory, recursive: true);
    }
    private sealed class AllowAssessment : IProxySubmissionAssessment
    {
        public ValueTask<ProxySubmissionAssessmentResult> AssessAsync(IStagedProxySubmission s, CancellationToken token)
            => ValueTask.FromResult(new ProxySubmissionAssessmentResult { Disposition = ProxySubmissionDisposition.Allow, IsDeterministic = true });
    }
    private sealed class WarningSink : IProxyWarningEventSink
    {
        public ValueTask RecordOutcomeAsync(ProxyWarningAnalysisOutcome outcome, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RecordMarkerAsync(ProxyDeterministicWarningEvent marker, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class WarningAnalyzer : IProxyWarningAnalyzer
    {
        public ValueTask<ProxyWarningAnalysisResult> AnalyzeAsync(ProxyWarningAnalysisInput input, CancellationToken token)
            => ValueTask.FromResult(new ProxyWarningAnalysisResult
            {
                MessageToken = "fixture-message", ThreadToken = "fixture-thread", Findings = [],
                Availability = ProxyWarningAvailability.NotProduced, AvailabilityReason = ProxyWarningAvailabilityReason.NoAnalyserConfigured,
            });
    }
}
