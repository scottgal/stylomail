using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StyloMail.Queue;
using StyloMail.Transport.Delivery;
using StyloMail.Transport.Ingress;

namespace StyloMail.Host.Hosting;

/// <summary>
/// Runs the SMTP submission listener for the lifetime of the host, if this deployment enabled it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enabled is decided here, not at registration.</b> The host's composition root runs before the
/// test host layers its own configuration in, so a registration-time decision reads the wrong
/// values — and the whole point of a listener that defaults to off is that enabling it is a
/// deliberate act. The decision therefore belongs where the configuration is final.
/// </para>
/// <para>
/// The listener's own <c>StopAsync</c> waits for in-flight sessions rather than abandoning them, and
/// that is the property this wrapper exists to preserve: a session interrupted between the queue
/// commit and its <c>250</c> has accepted mail it never acknowledged, and the client — having
/// received nothing — will send it again. Waiting is what keeps the duplicate an ambiguity rather
/// than a certainty.
/// </para>
/// <para>
/// <see cref="StartAsync"/> deliberately does not forward the startup token. That token is cancelled
/// when the host finishes starting, so a listener linked to it would stop the moment the host came
/// up. Shutdown is signalled through <see cref="StopAsync"/>, which is the callback for it.
/// </para>
/// <para>
/// <b>Stopped and then disposed, which is the ordinary hosted-service shape.</b> That pair used to
/// throw: <see cref="SmtpSubmissionListener.DisposeAsync"/> reached its second stop-call after
/// <c>StopAsync</c> had already nulled the socket reference, returned early without joining the
/// drain, and freed the connection semaphore that draining sessions were still returning. This
/// component worked around it by stopping without disposing, and the workaround is gone because
/// <c>transport-</c> fixed the cause — a second stop-caller now waits on the same drain task. The
/// history is kept here rather than deleted because the lesson is the lifecycle one: a component
/// that can only be stopped <em>or</em> disposed, never both, is a component that cannot be hosted.
/// </para>
/// </remarks>
public sealed class SmtpIngressHostedService : IHostedService, IAsyncDisposable
{
    private readonly HostTransportOptions _transport;
    private readonly ISmtpIngressSink _sink;
    private readonly ISubmissionAuthenticator _authenticator;
    private readonly TimeProvider _clock;

    private SmtpSubmissionListener? _listener;

    public SmtpIngressHostedService(
        IOptions<HostTransportOptions> transport,
        ISmtpIngressSink sink,
        ISubmissionAuthenticator authenticator,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(clock);

        _transport = transport.Value;
        _sink = sink;
        _authenticator = authenticator;
        _clock = clock;
    }

    /// <summary>The port actually bound, or null when the listener is not running.</summary>
    /// <remarks>Meaningful because a configured port of 0 asks the operating system to choose.</remarks>
    public int? BoundPort => _listener?.BoundPort;

    public bool IsRunning => _listener is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_transport.SmtpIngress.Enabled)
        {
            return Task.CompletedTask;
        }

        // Built here so a misconfiguration — encryption required with no certificate, a ServerName
        // missing from LocalHostIdentities — refuses to start the host rather than appearing as
        // rejected mail later.
        _listener = new SmtpSubmissionListener(
            _transport.SmtpIngress.Build(),
            _sink,
            _authenticator,
            _clock);

        _listener.Start(CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the listener and waits for the sessions it is mid-conversation with.
    /// </summary>
    /// <remarks>
    /// The wait is the point. A session interrupted between the queue commit and its <c>250</c> has
    /// accepted mail it never acknowledged, and the client — having heard nothing — will send it
    /// again. Letting those sessions finish is what keeps that duplicate an ambiguity rather than a
    /// certainty.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken)
        => _listener is null ? Task.CompletedTask : _listener.StopAsync();

    /// <summary>
    /// Releases the listener once the host has stopped it.
    /// </summary>
    /// <remarks>
    /// This runs after <see cref="StopAsync"/>, which is the order the host guarantees and the order
    /// the listener now requires — see the class remarks for what that pair used to do.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }
}

/// <summary>
/// Runs the queue's delivery worker in-process for the lifetime of the host, if there is somewhere
/// to deliver to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stopping token is the whole point.</b> <see cref="QueueDeliveryWorker.RunAsync"/> registers
/// on the token it is given and, when it fires, stops claiming immediately but lets an in-flight
/// delivery run out its <see cref="QueueDeliveryWorkerOptions.DrainTimeout"/>. Pass the wrong token —
/// or none — and that bounded drain never starts: the process either hangs on a wedged upstream or
/// cuts a delivery off mid-flight, and the second of those creates the duplicate the drain exists to
/// avoid. <see cref="BackgroundService.ExecuteAsync"/> receives exactly the token cancelled when the
/// host begins shutting down, so it is forwarded unchanged and nothing here decides anything.
/// </para>
/// <para>
/// <b>Why an unconfigured deployment runs no worker rather than a refusing one.</b> A worker with no
/// delivery port would lease every accepted message and burn its retry budget against a target that
/// cannot work, ending in a terminal failure for mail that was perfectly good — mail loss caused by
/// the retry policy itself. Not running is the honest alternative, and
/// <see cref="HostServices.DescribeTransport"/> says so at startup so it is not a silent absence.
/// </para>
/// <para>
/// The port is built here rather than registered, because whether one exists at all is the same
/// question as whether the worker runs. A container registration would have to answer it at
/// registration time — before the configuration is final — or return a placeholder port that exists
/// only to be constructed and never used.
/// </para>
/// </remarks>
public sealed class QueueDeliveryHostedService : BackgroundService
{
    private readonly HostTransportOptions _transport;
    private readonly QueueStore _store;
    private readonly QueueOptions _queueOptions;
    private readonly QueueDeliveryWorkerOptions _workerOptions;
    private readonly TimeProvider _clock;

    private SmtpDeliveryPort? _port;

    public QueueDeliveryHostedService(
        IOptions<HostTransportOptions> transport,
        QueueStore store,
        QueueOptions queueOptions,
        QueueDeliveryWorkerOptions workerOptions,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queueOptions);
        ArgumentNullException.ThrowIfNull(workerOptions);
        ArgumentNullException.ThrowIfNull(clock);

        _transport = transport.Value;
        _store = store;
        _queueOptions = queueOptions;
        _workerOptions = workerOptions;
        _clock = clock;
    }

    /// <summary>The worker id leases from this process are attributed to, or null when not running.</summary>
    public string? WorkerId { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_transport.Upstream.IsConfigured)
        {
            return;
        }

        _port = new SmtpDeliveryPort(_transport.Upstream.Build(), _clock);

        var worker = new QueueDeliveryWorker(_store, _port, _queueOptions, _workerOptions);
        WorkerId = worker.WorkerId;

        await worker.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The base wait comes first: it is what lets the worker's bounded drain finish an in-flight
        // delivery before the connection it is using is taken away.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_port is not null)
        {
            await _port.DisposeAsync().ConfigureAwait(false);
            _port = null;
        }
    }
}
