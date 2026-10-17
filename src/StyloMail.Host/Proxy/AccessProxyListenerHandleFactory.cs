using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Host.Proxy;

/// <summary>Loads the deployment certificate a TLS-required listener presents.</summary>
/// <remarks>
/// The certificate reference is a deployment path and is never emitted by a route. An unreadable,
/// absent or password-less certificate is a startup fault for that listener, not a reason to serve
/// plaintext: downgrading the transport to make a configuration mistake disappear is the one
/// outcome a mail proxy must never choose.
/// </remarks>
public sealed class HostProxyCertificateResolver : IProxyCertificateResolver
{
    private readonly IOptions<HostProxyOptions> _options;

    public HostProxyCertificateResolver(IOptions<HostProxyOptions> options) => _options = options;

    public ValueTask<X509Certificate2?> ResolveAsync(
        string certificateReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(certificateReference))
        {
            return ValueTask.FromResult<X509Certificate2?>(null);
        }

        if (!File.Exists(certificateReference))
        {
            return ValueTask.FromResult<X509Certificate2?>(null);
        }

        var password = _options.Value.CertificatePasswordEnvironmentVariable is { Length: > 0 } variable
            ? Environment.GetEnvironmentVariable(variable)
            : null;

        var certificate = Path.GetExtension(certificateReference)
            .Equals(".pem", StringComparison.OrdinalIgnoreCase)
            ? X509Certificate2.CreateFromPemFile(certificateReference)
            : X509CertificateLoader.LoadPkcs12FromFile(certificateReference, password);

        return ValueTask.FromResult<X509Certificate2?>(certificate);
    }
}

/// <summary>
/// Composes access's published protocol session runner for one configured listener.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every dependency is checked before a socket is bound.</b> A listener that starts and then
/// fails each session at authentication or at the backend is worse than one that refuses to start:
/// the first looks like a product that is running and broken, the second says exactly which
/// composition is missing. So a missing backend connector factory, certificate, or SMTP
/// assessment/stager raises here and the supervisor records the listener as faulted.
/// </para>
/// <para>
/// No credential, certificate path or backend endpoint appears in the failure text. The route
/// reports the category, and the detail an operator needs is in the listener's fault reason.
/// </para>
/// </remarks>
public sealed class AccessProxyListenerHandleFactory : IProxyListenerHandleFactory
{
    private readonly IServiceProvider _services;
    private readonly IStyloMailAccountStore _accounts;
    private readonly IStyloMailPasswordHasher _passwordHasher;
    private readonly IBackendCredentialStore _credentialStore;
    private readonly BackendCredentialResolver _credentialResolver;
    private readonly SessionLimiter _limiter;
    private readonly AccessProxyBounds _bounds;
    private readonly TimeProvider _clock;
    private readonly IProxyCertificateResolver _certificates;
    private readonly IProxyListenerEventSink _events;
    private readonly IProxySessionAuthenticator _sessionAuthenticator;
    private readonly ILogger<AccessProxyListenerHandleFactory> _logger;
    private int _telemetryFailures;

    public AccessProxyListenerHandleFactory(
        IServiceProvider services,
        IStyloMailAccountStore accounts,
        IStyloMailPasswordHasher passwordHasher,
        IBackendCredentialStore credentialStore,
        BackendCredentialResolver credentialResolver,
        SessionLimiter limiter,
        AccessProxyBounds bounds,
        TimeProvider clock,
        IProxyCertificateResolver certificates,
        IProxyListenerEventSink events,
        IProxySessionAuthenticator sessionAuthenticator,
        ILogger<AccessProxyListenerHandleFactory> logger)
    {
        _services = services;
        _accounts = accounts;
        _passwordHasher = passwordHasher;
        _credentialStore = credentialStore;
        _credentialResolver = credentialResolver;
        _limiter = limiter;
        _bounds = bounds;
        _clock = clock;
        _certificates = certificates;
        _events = events;
        _sessionAuthenticator = sessionAuthenticator;
        _logger = logger;
    }

    public IProxyListenerHandle? Create(HostProxyListenerOptions listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        // The backend connector factory is not composed by default: it needs a transport that knows
        // each provider's endpoint, and inventing one here would pick a host that no deployment
        // configured. Its absence is a fault with a name, not a half-running listener.
        var connectors = _services.GetService<IProxyBackendConnectorFactory>()
            ?? throw new ProxyConfigurationException(
                "The proxy listener cannot start: no backend connector factory is composed.");

        if (!IPAddress.TryParse(listener.BindAddress, out var bindAddress))
        {
            throw new ProxyConfigurationException(
                "The proxy listener cannot start: its configured bind address is not an IP address.");
        }

        if (listener.TlsPolicy == ProxyTlsPolicy.LoopbackHarnessPlaintext && !IPAddress.IsLoopback(bindAddress))
        {
            throw new ProxyConfigurationException(
                "The proxy listener cannot start: plaintext is permitted only on a loopback address.");
        }

        TlsServerOptions? tls = null;
        if (listener.TlsPolicy == ProxyTlsPolicy.Required)
        {
            if (string.IsNullOrWhiteSpace(listener.CertificateReference))
            {
                throw new ProxyConfigurationException(
                    "The proxy listener cannot start: a TLS-required listener has no certificate reference.");
            }

            var certificate = _certificates
                .ResolveAsync(listener.CertificateReference, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult()
                ?? throw new ProxyConfigurationException(
                    "The proxy listener cannot start: its certificate could not be loaded.");

            tls = new TlsServerOptions { ServerCertificate = certificate };
        }

        var runnerOptions = new ProxyListenerOptions
        {
            TenantId = listener.TenantId,
            BindAddress = bindAddress,
            Port = listener.Port,
            Protocol = listener.Protocol,
            CertificateReference = listener.CertificateReference,
            TlsPolicy = listener.TlsPolicy,
            Enabled = true,
        };

        var assessment = _services.GetService<StyloMail.AccessProxy.Submission.IProxySubmissionAssessment>();
        var stager = _services.GetService<StyloMail.AccessProxy.Submission.IEphemeralSubmissionStager>();

        // Access's own factory checks TLS for SMTP; assessment and stager are checked only when the
        // first session runs, so they are checked here instead. A missing submission path must fault
        // the listener at enable, not every client's first DATA command.
        if (listener.Protocol == ProxyListenerProtocol.Smtp
            && (tls is null || assessment is null || stager is null))
        {
            throw new ProxyConfigurationException(
                "The proxy listener cannot start: SMTP requires TLS material and a submission assessment/stager.");
        }

        var dependencies = new ProxySessionRunnerDependencies
        {
            // The decorator attributes the account lookup to the connection currently running on
            // this async flow; access's session still only sees an account store.
            Accounts = new ProxySessionAccountStore(_accounts, OnSessionAuthenticatedAsync),
            PasswordHasher = _passwordHasher,
            CredentialStore = _credentialStore,
            CredentialResolver = _credentialResolver,
            BackendConnectors = connectors,
            Limiter = _limiter,
            Bounds = _bounds,
            TimeProvider = _clock,
            Tls = tls,
            SubmissionAssessment = assessment,
            SubmissionStager = stager,
            RetrievalObserver = _services.GetService<IRetrievalObserver>(),
        };

        var run = ProxySessionRunnerFactory.Create(runnerOptions, dependencies);

        return new AccessProxyListenerHandle(runnerOptions, run, tls, _events, _clock, _logger);
    }

    /// <summary>
    /// Records the account id on the session's existing opened row once the protocol session has
    /// resolved it.
    /// </summary>
    /// <remarks>
    /// This is an in-place update, not a second opened event: one session has one live row, and the
    /// route's live query reads that row. A failed write is swallowed because telemetry must never
    /// drop a user's session; the failure counter keeps the log bounded.
    /// </remarks>
    private async ValueTask OnSessionAuthenticatedAsync(string accountId)
    {
        if (ProxySessionContext.Current.Value is not { } binding)
        {
            return;
        }

        try
        {
            await _sessionAuthenticator.MarkAuthenticatedAsync(
                binding.TenantId,
                binding.SessionId,
                accountId,
                _clock.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var failures = Interlocked.Increment(ref _telemetryFailures);
            if (failures == 1 || failures % 100 == 0)
            {
                _logger.LogWarning(
                    "A proxy session authentication record could not be written ({Failures} total).",
                    failures);
            }
        }
    }

    private sealed class AccessProxyListenerHandle : IProxyListenerHandle
    {
        private readonly ProxyListenerOptions _options;
        private readonly Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> _run;
        private readonly TlsServerOptions? _tls;
        private readonly IProxyListenerEventSink _events;
        private readonly TimeProvider _clock;
        private readonly ILogger _logger;
        private TcpAccessProxyListener? _listener;
        private bool _started;

        public AccessProxyListenerHandle(
            ProxyListenerOptions options,
            Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> run,
            TlsServerOptions? tls,
            IProxyListenerEventSink events,
            TimeProvider clock,
            ILogger logger)
        {
            _options = options;
            _run = run;
            _tls = tls;
            _events = events;
            _clock = clock;
            _logger = logger;
        }

        public int? BoundPort => _started ? _listener?.BoundPort : null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _listener = new TcpAccessProxyListener(
                new IPEndPoint(_options.BindAddress, _options.Port),
                _options.TenantId,
                RunSessionAsync);

            return StartCoreAsync(cancellationToken);
        }

        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            await _listener!.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }

        private async Task<SessionOutcome> RunSessionAsync(
            string tenantId,
            IDuplexChannel channel,
            CancellationToken cancellationToken)
        {
            if (_tls is null)
            {
                return await TrackedAsync(tenantId, channel, cancellationToken).ConfigureAwait(false);
            }

            await using var startTls = new StartTlsServerDuplexChannel(channel.Input, _tls, channel.Description);
            return await TrackedAsync(tenantId, startTls, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SessionOutcome> TrackedAsync(
            string tenantId,
            IDuplexChannel channel,
            CancellationToken cancellationToken)
        {
            var sessionId = "ps_" + Guid.NewGuid().ToString("N");
            ProxySessionContext.Current.Value =
                new ProxySessionBinding(sessionId, tenantId, _options.Protocol);
            await PublishAsync(new ProxySessionEvent
            {
                Kind = ProxySessionEventKind.Opened,
                SessionId = sessionId,
                TenantId = tenantId,
                // The account is resolved inside the protocol session, after the client credential
                // is verified. The connection-level event deliberately carries no guessed account;
                // the account-store decorator publishes a follow-up opened event once it is known.
                AccountId = null,
                Protocol = _options.Protocol,
                Direction = ProxySessionDirection.ClientToProxy,
                Timestamp = _clock.GetUtcNow(),
            }).ConfigureAwait(false);

            SessionOutcome? outcome = null;
            try
            {
                outcome = await _run(tenantId, channel, cancellationToken).ConfigureAwait(false);
                return outcome.Value;
            }
            catch (Exception exception)
            {
                // The access listener swallows session exceptions by design. Without this line a
                // backend authentication failure is invisible: the client has already been told
                // 235, so the only symptom is a disconnect. The type and bounded message name the
                // stage; no credential, server line or address is included beyond the exception's
                // own bounded text.
                outcome = SessionOutcome.ProtocolError;
                _logger.LogWarning(
                    "Proxy session {SessionId} failed: {ExceptionType}: {Detail}",
                    sessionId,
                    exception.GetType().Name,
                    exception.Message.Length > 200 ? exception.Message[..200] : exception.Message);
                throw;
            }
            finally
            {
                ProxySessionContext.Current.Value = null;
                await PublishAsync(new ProxySessionEvent
                {
                    Kind = ProxySessionEventKind.Closed,
                    SessionId = sessionId,
                    TenantId = tenantId,
                    AccountId = null,
                    Protocol = _options.Protocol,
                    Direction = ProxySessionDirection.ClientToProxy,
                    Timestamp = _clock.GetUtcNow(),
                    Outcome = outcome,
                    BoundReason = ProxyBoundReason.None,
                }).ConfigureAwait(false);
            }
        }

        private async ValueTask PublishAsync(ProxySessionEvent sessionEvent)
        {
            try
            {
                await _events.PublishAsync(sessionEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Telemetry never decides whether a client session runs. The absence is visible as
                // not-produced on the management surface rather than as a refused connection.
                _logger.LogWarning("A proxy session event could not be recorded.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => _listener?.StopAsync(cancellationToken) ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_listener is not null)
            {
                await _listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
