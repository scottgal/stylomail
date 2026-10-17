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
    private TcpAccessProxyListener? _listener;

    public ProxyListenerHostedService(ProxyListenerOptions options, IProxyCertificateResolver certificates,
        IProxyListenerEventSink events,
        Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> sessions,
        TimeProvider clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
                async (tenant, channel, token) =>
                {
                    if (tls is null) return await _sessions(tenant, channel, token).ConfigureAwait(false);
                    await using var startTls = new StartTlsServerDuplexChannel(channel.Input, tls, channel.Description);
                    return await _sessions(tenant, startTls, token).ConfigureAwait(false);
                });
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
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
    }
}
