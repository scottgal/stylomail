using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>The listener's observable accept-loop state.</summary>
public enum TcpAccessProxyListenerState
{
    NotStarted = 0,
    Running = 1,
    Stopping = 2,
    Stopped = 3,
    Faulted = 4,
}

/// <summary>
/// Accepts client TCP connections and runs one access-proxy session for each.
/// </summary>
/// <remarks>
/// Binding and lifecycle are separate from protocol sessions. The Host supplies the configured
/// endpoint and session factory, retaining ownership of durable stores, TLS policy, management
/// telemetry and HTTP composition. This library owns only client sockets and their bounded stop path.
///
/// <para>
/// <b>This listener is plaintext TCP.</b> It does not terminate TLS and its protocol sessions refuse
/// STARTTLS. It is suitable for loopback harnesses only until a configured TLS terminator is supplied.
/// A caller must not present it as a secure network listener merely because its backend uses TLS.
/// </para>
/// </remarks>
public sealed class TcpAccessProxyListener : IAsyncDisposable
{
    private readonly IPEndPoint _endpoint;
    private readonly TcpListener _listener;
    private readonly string _tenantId;
    private readonly Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> _runSession;
    private readonly CancellationTokenSource _acceptingStopping = new();
    private readonly CancellationTokenSource _sessionStopping = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly Lock _lifecycle = new();
    private Task? _accepting;
    private Task? _acceptStopBegun;
    private long _nextSessionId;
    private bool _started;
    private int _state = (int)TcpAccessProxyListenerState.NotStarted;
    private int _disposed;

    /// <summary>Creates an unstarted listener for the configured endpoint.</summary>
    public TcpAccessProxyListener(
        IPEndPoint endpoint,
        string tenantId,
        Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> runSession)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(runSession);

        _endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
        _tenantId = tenantId;
        _listener = new TcpListener(_endpoint);
        _runSession = runSession;
    }

    /// <summary>The configured bind endpoint.</summary>
    public IPEndPoint Endpoint => _endpoint;

    /// <summary>
    /// The configured tenant for every connection accepted by this listener.
    /// </summary>
    /// <remarks>
    /// Tenant selection is a listener configuration boundary, never a lookup from a supplied login.
    /// A multi-tenant Host composes one listener per tenant and bind endpoint.
    /// </remarks>
    public string TenantId => _tenantId;

    /// <summary>The port actually bound, including an operating-system-assigned port for zero.</summary>
    /// <exception cref="InvalidOperationException">The listener has not started.</exception>
    public int BoundPort
        => _started ? ((IPEndPoint)_listener.LocalEndpoint).Port
            : throw new InvalidOperationException("The listener has not started.");

    /// <summary>
    /// The accept-loop state for Host readiness and fault reporting.
    /// </summary>
    /// <remarks>
    /// A non-stop accept exception moves this to <see cref="TcpAccessProxyListenerState.Faulted"/>,
    /// so a bound port is never mistaken for a live accept loop.
    /// </remarks>
    public TcpAccessProxyListenerState State
        => (TcpAccessProxyListenerState)Volatile.Read(ref _state);

    /// <summary>Starts accepting connections.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        lock (_lifecycle)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            if (_acceptStopBegun is not null)
            {
                throw new InvalidOperationException("A stopped listener cannot be restarted.");
            }

            _listener.Start();
            _started = true;
            Volatile.Write(ref _state, (int)TcpAccessProxyListenerState.Running);
            _accepting = AcceptAsync();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Compatibility helper for simple callers that need an immediately running listener.
    /// </summary>
    public static TcpAccessProxyListener Start(
        IPAddress bindAddress,
        int port,
        string tenantId,
        Func<string, IDuplexChannel, CancellationToken, Task<SessionOutcome>> runSession)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        if (port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Must be a TCP port number.");
        }

        var listener = new TcpAccessProxyListener(new IPEndPoint(bindAddress, port), tenantId, runSession);
        listener.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return listener;
    }

    private async Task AcceptAsync()
    {
        while (!_acceptingStopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_acceptingStopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_acceptingStopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_acceptingStopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                Volatile.Write(ref _state, (int)TcpAccessProxyListenerState.Faulted);
                return;
            }
            catch (Exception)
            {
                Volatile.Write(ref _state, (int)TcpAccessProxyListenerState.Faulted);
                return;
            }

            var sessionId = Interlocked.Increment(ref _nextSessionId);
            _clients[sessionId] = client;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessions[sessionId] = completion.Task;
            _ = RunTrackedSessionAsync(sessionId, client, completion);
        }
    }

    private async Task RunTrackedSessionAsync(
        long sessionId,
        TcpClient client,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await ServeAsync(sessionId, client).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Session failures are isolated from the accept loop. Host telemetry is supplied through
            // the injected session factory, rather than by this socket owner.
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            _clients.TryRemove(sessionId, out _);
            completion.TrySetResult(true);
        }
    }

    private async Task ServeAsync(long sessionId, TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            // This can reach a session factory and subsequently an event, so it intentionally carries
            // only a random opaque connection identifier, never the peer address or port.
            var description = $"client-session:{Guid.NewGuid():N}";

            await using var channel = new StreamDuplexChannel(stream, stream, description);
            await _runSession(_tenantId, channel, _sessionStopping.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting and drains active sessions until the caller's bound expires.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await BeginStopAcceptingAsync().ConfigureAwait(false);
        await AwaitAcceptStoppedAsync().ConfigureAwait(false);
        var sessions = Task.WhenAll(_sessions.Values);

        try
        {
            await sessions.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AbortSessionsAsync().ConfigureAwait(false);
            await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
            throw;
        }
    }

    private Task BeginStopAcceptingAsync()
    {
        lock (_lifecycle)
        {
            return _acceptStopBegun ??= BeginStopAcceptingCoreAsync();
        }
    }

    private async Task BeginStopAcceptingCoreAsync()
    {
        if (State == TcpAccessProxyListenerState.Running)
        {
            Volatile.Write(ref _state, (int)TcpAccessProxyListenerState.Stopping);
        }

        await _acceptingStopping.CancelAsync().ConfigureAwait(false);

        if (_started)
        {
            _listener.Stop();
        }
    }

    private async Task AwaitAcceptStoppedAsync()
    {
        if (_accepting is not null)
        {
            await _accepting.ConfigureAwait(false);
        }

        if (State == TcpAccessProxyListenerState.Stopping)
        {
            Volatile.Write(ref _state, (int)TcpAccessProxyListenerState.Stopped);
        }
    }

    private async Task AbortSessionsAsync()
    {
        await _sessionStopping.CancelAsync().ConfigureAwait(false);
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Shutdown order is intentional: stop accepting, close active clients, wait for the accept
        // loop, then drain its session tasks. Closing before awaiting the accept loop lets a stalled
        // protocol read observe shutdown instead of holding disposal hostage.
        await BeginStopAcceptingAsync().ConfigureAwait(false);
        await AbortSessionsAsync().ConfigureAwait(false);
        await AwaitAcceptStoppedAsync().ConfigureAwait(false);
        await AbortSessionsAsync().ConfigureAwait(false);
        await Task.WhenAll(_sessions.Values).ConfigureAwait(false);
        _acceptingStopping.Dispose();
        _sessionStopping.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TcpAccessProxyListener));
        }
    }
}
