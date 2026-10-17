using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>The runtime state of one configured listener.</summary>
public enum ProxyListenerRuntimeState
{
    /// <summary>The listener runtime is not composed, so no state was produced.</summary>
    NotProduced = 0,
    Started = 1,
    Stopping = 2,
    Stopped = 3,
    Faulted = 4,
}

/// <summary>A listener's read-only management view plus its drain state.</summary>
public sealed record ProxyListenerSnapshot
{
    public required string ListenerId { get; init; }

    public required ProxyListenerProtocol Protocol { get; init; }

    /// <summary>The configured port, or the actually bound port once started.</summary>
    public required int BindPort { get; init; }

    public required ProxyTlsPolicy TlsPolicy { get; init; }

    public required bool Enabled { get; init; }

    public required string State { get; init; }

    public string? FaultReason { get; init; }

    public string? SafeDetail { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>
    /// True while a disable has stopped new sessions but existing ones are still reaching their
    /// bound. A disable is never a kill: this is what the operator is shown instead.
    /// </summary>
    public bool Draining { get; init; }
}

/// <summary>The Host-owned control surface the listener routes call.</summary>
public interface IProxyListenerControl
{
    IReadOnlyList<ProxyListenerSnapshot> Snapshot(string tenantId);

    /// <summary>Starts a listener, or returns null when no such listener exists for this tenant.</summary>
    ValueTask<ProxyListenerSnapshot?> EnableAsync(
        string tenantId,
        string listenerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops accepting new sessions and lets existing ones reach their bound. Returns as soon as
    /// the drain has been started, not when it finishes.
    /// </summary>
    ValueTask<ProxyListenerSnapshot?> DisableAsync(
        string tenantId,
        string listenerId,
        CancellationToken cancellationToken);
}

/// <summary>A runnable listener bound to one configured endpoint.</summary>
public interface IProxyListenerHandle : IAsyncDisposable
{
    /// <summary>The actually bound port, once started.</summary>
    int? BoundPort { get; }

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops accepting and drains active sessions until the token expires.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates the runnable listener for a configured endpoint.
/// </summary>
/// <remarks>
/// The Host default is <see cref="UnavailableProxyListenerHandleFactory"/>: the protocol session
/// composition lives in <c>StyloMail.AccessProxy</c>, so this deployment has no honest way to accept
/// a client session yet and reports <c>not_produced</c> rather than binding a socket that cannot
/// authenticate anyone. A deployment that composes sessions supplies a factory.
/// </remarks>
public interface IProxyListenerHandleFactory
{
    IProxyListenerHandle? Create(HostProxyListenerOptions listener);
}

/// <summary>The factory for a deployment that has not composed a protocol session.</summary>
public sealed class UnavailableProxyListenerHandleFactory : IProxyListenerHandleFactory
{
    public IProxyListenerHandle? Create(HostProxyListenerOptions listener) => null;
}

/// <summary>
/// Owns listener enable/disable, including drain-aware disable.
/// </summary>
/// <remarks>
/// Enable is a start of a fresh listener, because the access listener deliberately cannot be
/// restarted once stopped: a stopped listener's cancellation state is spent, and pretending
/// otherwise would produce a listener that looks running and accepts nothing. Disable returns while
/// existing sessions are still finishing; it asks the listener to stop accepting, then leaves the
/// drain to its own bounded task. A disable that blocked the HTTP request until the last client
/// disconnected would be indistinguishable from a kill to the operator, and would time out long
/// before a mail client's session does.
/// </remarks>
public sealed class ProxyListenerSupervisor : IProxyListenerControl, IHostedService, IAsyncDisposable
{
    private readonly IOptions<HostProxyOptions> _options;
    private readonly IProxyListenerHandleFactory _handles;
    private readonly IProxyListenerEventSink _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProxyListenerSupervisor> _logger;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly Dictionary<string, Entry> _listeners = new(StringComparer.Ordinal);

    public ProxyListenerSupervisor(
        IOptions<HostProxyOptions> options,
        IProxyListenerHandleFactory handles,
        IProxyListenerEventSink events,
        TimeProvider clock,
        ILogger<ProxyListenerSupervisor> logger)
    {
        _options = options;
        _handles = handles;
        _events = events;
        _clock = clock;
        _logger = logger;

        foreach (var listener in options.Value.Listeners)
        {
            if (string.IsNullOrWhiteSpace(listener.ListenerId))
            {
                continue;
            }

            _listeners[listener.ListenerId] = new Entry(listener);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _listeners.Values.Where(e => e.Options.Enabled).ToArray())
        {
            await EnableAsync(entry.Options.TenantId, entry.Options.ListenerId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _listeners.Values)
        {
            entry.Drain?.Cancel();
            if (entry.Handle is not null)
            {
                try
                {
                    await entry.Handle.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutdown is best effort; the listener's own bound is what protects clients.
                }
            }
        }
    }

    public IReadOnlyList<ProxyListenerSnapshot> Snapshot(string tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        return [.. _listeners.Values
            .Where(entry => string.Equals(entry.Options.TenantId, tenantId, StringComparison.Ordinal))
            .OrderBy(entry => entry.Options.ListenerId, StringComparer.Ordinal)
            .Select(entry => entry.Snapshot())];
    }

    public async ValueTask<ProxyListenerSnapshot?> EnableAsync(
        string tenantId,
        string listenerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(listenerId);

        await _mutate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryFind(tenantId, listenerId, out var entry))
            {
                return null;
            }

            if (entry.State is ProxyListenerRuntimeState.Started or ProxyListenerRuntimeState.Stopping)
            {
                return entry.Snapshot();
            }

            IProxyListenerHandle? handle;
            try
            {
                handle = _handles.Create(entry.Options);
            }
            catch (Exception)
            {
                // A missing composition dependency is named as a fault instead of a half-started
                // listener. The exception text can name a path or an endpoint, so only the category
                // reaches the management view.
                entry.Handle = null;
                entry.Set(ProxyListenerRuntimeState.Faulted, observedAt: _clock.GetUtcNow(),
                    faultReason: ProxyListenerFaultReason.StartupConfigurationInvalid,
                    safeDetail: "listener runtime dependencies are incomplete");
                await PublishAsync(entry, ProxyListenerEventKind.Faulted,
                    ProxyListenerFaultReason.StartupConfigurationInvalid,
                    "listener runtime dependencies are incomplete", cancellationToken).ConfigureAwait(false);
                _logger.LogError("Proxy listener {ListenerId} could not be composed.", listenerId);
                return entry.Snapshot();
            }

            if (handle is null)
            {
                entry.Set(ProxyListenerRuntimeState.NotProduced, observedAt: _clock.GetUtcNow(),
                    safeDetail: "the listener runtime is not composed in this deployment");
                return entry.Snapshot();
            }

            try
            {
                await handle.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The exception text can carry a path or an endpoint, so it is deliberately not
                // copied into the management view. The reason an operator can act on is the category.
                await handle.DisposeAsync().ConfigureAwait(false);
                entry.Handle = null;
                entry.Set(ProxyListenerRuntimeState.Faulted, observedAt: _clock.GetUtcNow(),
                    faultReason: ProxyListenerFaultReason.StartupConfigurationInvalid,
                    safeDetail: "listener failed to start");
                await PublishAsync(entry, ProxyListenerEventKind.Faulted,
                    ProxyListenerFaultReason.StartupConfigurationInvalid, "listener failed to start",
                    cancellationToken).ConfigureAwait(false);
                _logger.LogError("Proxy listener {ListenerId} failed to start.", listenerId);
                return entry.Snapshot();
            }

            entry.Handle = handle;
            entry.Drain = null;
            entry.Set(ProxyListenerRuntimeState.Started, observedAt: _clock.GetUtcNow());
            await PublishAsync(entry, ProxyListenerEventKind.Started, ProxyListenerFaultReason.None, null,
                cancellationToken).ConfigureAwait(false);
            return entry.Snapshot();
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async ValueTask<ProxyListenerSnapshot?> DisableAsync(
        string tenantId,
        string listenerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(listenerId);

        await _mutate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryFind(tenantId, listenerId, out var entry))
            {
                return null;
            }

            if (entry.Handle is null)
            {
                entry.Set(ProxyListenerRuntimeState.Stopped, observedAt: _clock.GetUtcNow());
                return entry.Snapshot();
            }

            var handle = entry.Handle;
            entry.Set(ProxyListenerRuntimeState.Stopping, observedAt: _clock.GetUtcNow());
            entry.Drain = new CancellationTokenSource(_options.Value.DrainTimeout);
            var drainToken = entry.Drain.Token;

            // The drain is deliberately detached from the request: disable means "refuse new
            // sessions", not "hold this HTTP response until every client goes away".
            entry.DrainTask = Task.Run(async () =>
            {
                try
                {
                    await handle.StopAsync(drainToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The drain exceeded its ceiling. The listener's own session bound is the
                    // primary protection; this is the backstop, and it disposes rather than leaks.
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _logger.LogError("Proxy listener {ListenerId} drain failed.", listenerId);
                }
                finally
                {
                    entry.Handle = null;
                    entry.Drain = null;
                    entry.DrainTask = null;
                    entry.Set(ProxyListenerRuntimeState.Stopped, observedAt: _clock.GetUtcNow());
                    await PublishAsync(entry, ProxyListenerEventKind.Stopped,
                        ProxyListenerFaultReason.None, null, CancellationToken.None).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            return entry.Snapshot();
        }
        finally
        {
            _mutate.Release();
        }
    }

    private bool TryFind(string tenantId, string listenerId, out Entry entry)
    {
        if (_listeners.TryGetValue(listenerId, out var found)
            && string.Equals(found.Options.TenantId, tenantId, StringComparison.Ordinal))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }

    private async ValueTask PublishAsync(
        Entry entry,
        ProxyListenerEventKind kind,
        ProxyListenerFaultReason fault,
        string? detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await _events.PublishAsync(new ProxyListenerEvent
            {
                Kind = kind,
                TenantId = entry.Options.TenantId,
                Protocol = entry.Options.Protocol,
                Timestamp = _clock.GetUtcNow(),
                BoundPort = entry.Handle?.BoundPort,
                FaultReason = fault,
                SafeDetail = detail,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Telemetry must not decide whether a listener runs. A failed event write is reported
            // by the availability of operations data, not by refusing the operator's control.
            _logger.LogWarning("Proxy listener event could not be recorded for {ListenerId}.", entry.Options.ListenerId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _listeners.Values)
        {
            entry.Drain?.Cancel();
            if (entry.Handle is not null)
            {
                await entry.Handle.DisposeAsync().ConfigureAwait(false);
            }
        }

        _mutate.Dispose();
    }

    private sealed class Entry
    {
        public Entry(HostProxyListenerOptions options)
        {
            Options = options;
            // A configured listener the host has not started is known to be stopped. It is not
            // "not produced": not-produced is reserved for a deployment with no runtime at all.
            State = ProxyListenerRuntimeState.Stopped;
            Enabled = options.Enabled;
        }

        public HostProxyListenerOptions Options { get; }

        public ProxyListenerRuntimeState State { get; private set; }

        public bool Enabled { get; private set; }

        public string? FaultReason { get; private set; }

        public string? SafeDetail { get; private set; }

        public DateTimeOffset? ObservedAt { get; private set; }

        public IProxyListenerHandle? Handle { get; set; }

        public CancellationTokenSource? Drain { get; set; }

        public Task? DrainTask { get; set; }

        public void Set(
            ProxyListenerRuntimeState state,
            DateTimeOffset? observedAt = null,
            ProxyListenerFaultReason? faultReason = null,
            string? safeDetail = null)
        {
            State = state;
            if (observedAt is not null)
            {
                ObservedAt = observedAt;
            }

            FaultReason = faultReason is { } reason && reason != ProxyListenerFaultReason.None
                ? reason.ToString()
                : null;
            SafeDetail = safeDetail;
            Enabled = state switch
            {
                ProxyListenerRuntimeState.Started or ProxyListenerRuntimeState.Faulted => true,
                ProxyListenerRuntimeState.NotProduced => Options.Enabled,
                // Stopping is an operator's disable already in force: new sessions are refused, so
                // the readback must not say the listener is enabled while it drains.
                _ => false,
            };
        }

        public ProxyListenerSnapshot Snapshot() => new()
        {
            ListenerId = Options.ListenerId,
            Protocol = Options.Protocol,
            BindPort = Handle?.BoundPort ?? Options.Port,
            TlsPolicy = Options.TlsPolicy,
            Enabled = Enabled,
            State = State switch
            {
                ProxyListenerRuntimeState.Started => "started",
                ProxyListenerRuntimeState.Stopping => "stopping",
                ProxyListenerRuntimeState.Stopped => "stopped",
                ProxyListenerRuntimeState.Faulted => "faulted",
                _ => "not_produced",
            },
            FaultReason = FaultReason,
            SafeDetail = SafeDetail,
            ObservedAt = ObservedAt,
            Draining = State == ProxyListenerRuntimeState.Stopping,
        };
    }
}
