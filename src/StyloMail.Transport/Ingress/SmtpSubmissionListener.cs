using System.Net;
using System.Net.Sockets;

namespace StyloMail.Transport.Ingress;

/// <summary>
/// A restricted SMTP listener that accepts submissions from authenticated principals and handoffs
/// from a trusted internal relay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a public MX service.</b> The spec is categorical that StyloMail sits behind an established
/// MTA and does not take on internet-facing protocol complexity — no MX resolution, no reputation
/// management, no DSN generation. What this provides is the one integration the spec does allow:
/// a trusted handoff, or an authenticated submission relayed to a configured upstream.
/// </para>
/// <para>
/// Its entire job is the boundary. It reads a message under hard bounds, decides whether the peer is
/// authorised to hand us this message for this recipient, and then asks the composition root to make
/// it durable — answering <c>250</c> only if that succeeded. It does not assess, does not choose an
/// action, and does not deliver.
/// </para>
/// <para>
/// <b>The <c>250</c> is the whole point.</b> RFC 5321 makes it a transfer of responsibility: once
/// sent, the client may delete its copy. So it is emitted from exactly one place, downstream of a
/// <see cref="IngressDecision.Accepted"/> that names a durable queue row, and every other path —
/// including an unexpected exception from the sink — answers with a temporary failure.
/// </para>
/// </remarks>
public sealed class SmtpSubmissionListener : IAsyncDisposable
{
    private readonly SmtpIngressOptions _options;
    private readonly ISmtpIngressSink _sink;
    private readonly ISubmissionAuthenticator? _authenticator;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connectionLimit;
    private readonly Lock _gate = new();

    /// <summary>
    /// Guards <see cref="_stopTask"/>. Separate from <see cref="_gate"/> so that starting a stop
    /// while holding this lock cannot contend with the drain's own use of the other one.
    /// </summary>
    private readonly Lock _stopGate = new();

    private readonly List<Task> _connections = [];

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>The in-progress or completed drain, shared by every caller of <see cref="StopAsync"/>.</summary>
    private Task? _stopTask;

    private bool _disposed;

    public SmtpSubmissionListener(
        SmtpIngressOptions options,
        ISmtpIngressSink sink,
        ISubmissionAuthenticator? authenticator = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        options.Validate();

        _options = options;
        _sink = sink;
        _authenticator = authenticator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionLimit = new SemaphoreSlim(_options.MaxConcurrentConnections, _options.MaxConcurrentConnections);
    }

    /// <summary>The port actually bound. Useful when the configured port was 0.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Starts accepting connections.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
        {
            throw new InvalidOperationException("The listener is already running.");
        }

        var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Start();

        // A restart must not inherit the previous run's completed drain, or the next StopAsync would
        // return that stale task instead of stopping the listener that is running now.
        lock (_stopGate)
        {
            _stopTask = null;
        }

        _listener = listener;
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops accepting and waits for in-flight sessions to finish.
    /// </summary>
    /// <remarks>
    /// <b>Every caller gets the same task, so a second caller waits for the drain rather than
    /// returning early.</b> That is not a nicety: <see cref="DisposeAsync"/> is a second caller on
    /// the ordinary host-shutdown path — <c>IHostedService</c> stops and then disposes — and an
    /// early return there disposes the connection semaphore while the drain is still using it. The
    /// in-flight session's <c>finally</c> then calls <c>Release</c> on a disposed semaphore and the
    /// exception surfaces out of the first caller's <c>Task.WhenAll</c>, turning an orderly shutdown
    /// into a crash.
    ///
    /// <para>
    /// Idempotence is what makes <see cref="DisposeAsync"/> safe to pair with an explicit stop,
    /// rather than the pairing being a sequencing rule callers have to remember.
    /// </para>
    /// </remarks>
    public Task StopAsync()
    {
        lock (_stopGate)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        _listener = null;

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        listener.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected on shutdown.
            }
        }

        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        // In-flight sessions are waited for rather than abandoned. A session that is mid-acceptance
        // must be allowed to finish, because interrupting it between the queue commit and the 250
        // would leave a message accepted and unacknowledged.
        //
        // Every session's `finally` releases its connection slot before its task completes, so once
        // this returns no caller is left holding a reference to `_connectionLimit`. That ordering is
        // what makes the disposal in DisposeAsync safe rather than merely usually-fine.
        await Task.WhenAll(connections).ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Awaits the same task an explicit StopAsync call would, so disposing after stopping
            // waits for the drain instead of racing it.
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLimit.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                // The connection limit is acquired before accepting, so the backlog applies
                // backpressure at the socket rather than letting us build an unbounded set of
                // half-served clients.
                await _connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                _connectionLimit.Release();
                return;
            }

            var task = ServeOneAsync(client, cancellationToken);

            lock (_gate)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task ServeOneAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await SmtpIngressSession.RunAsync(
                client,
                _options,
                _sink,
                _authenticator,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLimit.Release();
        }
    }
}
