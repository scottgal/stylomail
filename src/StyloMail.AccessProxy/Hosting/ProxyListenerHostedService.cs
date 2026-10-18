using System.Net;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Hosting;

/// <summary>Tenant-bound listener lifecycle with explicit TLS-policy startup validation.</summary>
public sealed class ProxyListenerHostedService : IProxyListenerHostedService, IAsyncDisposable
{
    private readonly ProxyListenerOptions _options;
    private readonly IProxyCertificateResolver _certificates;
    private readonly IProxyListenerEventSink _events;
    private readonly Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> _sessions;
    private readonly TimeProvider _clock;
    private readonly AccessProxyBounds _bounds;
    private readonly SessionLimiter _limiter;
    private readonly bool _ownsLimiter;
    private int _disposed;
    private TcpAccessProxyListener? _listener;

    public ProxyListenerHostedService(ProxyListenerOptions options, IProxyCertificateResolver certificates,
        IProxyListenerEventSink events,
        Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> sessions,
        TimeProvider clock, AccessProxyBounds? bounds = null, SessionLimiter? limiter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _bounds = bounds ?? limiter?.Bounds ?? new AccessProxyBounds();
        _bounds.Validate();
        if (limiter is not null && limiter.Bounds != _bounds)
            throw new ArgumentException("Listener and session limiter bounds must match.", nameof(limiter));
        _limiter = limiter ?? new SessionLimiter(_bounds);
        _ownsLimiter = limiter is null;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        try
        {
            ValidatePolicy();
            TlsServerOptions? tls = null;
            if (_options.TlsPolicy == ProxyTlsPolicy.Required)
            {
                var reference = _options.CertificateReference
                    ?? throw new InvalidOperationException("TLS-required proxy listener has no certificate reference.");
                var certificate = await _certificates.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("TLS-required proxy listener certificate was unavailable.");
                tls = new TlsServerOptions { ServerCertificate = certificate };
            }

            _listener = new TcpAccessProxyListener(
                new IPEndPoint(_options.BindAddress, _options.Port), _options.TenantId,
                (tenant, channel, token) => ProxySessionRunnerFactory.RunWithTlsAsync(
                    _options.Protocol, tls, _bounds, _clock, _sessions, tenant, channel, token), _limiter);
            await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(new ProxyListenerEvent
            {
                Kind = ProxyListenerEventKind.Started, TenantId = _options.TenantId, Protocol = _options.Protocol,
                Timestamp = _clock.GetUtcNow(), BoundPort = _listener.BoundPort,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _events.PublishAsync(new ProxyListenerEvent
            {
                Kind = ProxyListenerEventKind.Faulted, TenantId = _options.TenantId, Protocol = _options.Protocol,
                Timestamp = _clock.GetUtcNow(), FaultReason = ProxyListenerFaultReason.StartupConfigurationInvalid,
                SafeDetail = "listener startup validation failed",
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null) return;
        await _listener.StopAsync(cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new ProxyListenerEvent
        {
            Kind = ProxyListenerEventKind.Stopped, TenantId = _options.TenantId, Protocol = _options.Protocol,
            Timestamp = _clock.GetUtcNow(), BoundPort = _listener.BoundPort,
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ValidatePolicy()
    {
        ArgumentException.ThrowIfNullOrEmpty(_options.TenantId);
        if (_options.Protocol == ProxyListenerProtocol.Smtp && _options.TlsPolicy != ProxyTlsPolicy.Required)
            throw new InvalidOperationException("SMTP submission requires TLS.");
        if (_options.TlsPolicy == ProxyTlsPolicy.LoopbackHarnessPlaintext && !IPAddress.IsLoopback(_options.BindAddress))
            throw new InvalidOperationException("Plaintext proxy listener is permitted only on loopback for harness use.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        if (_ownsLimiter) _limiter.Dispose();
    }
}
