using StyloMail.Core;

namespace StyloMail.Queue;

/// <summary>How one delivery cycle ended.</summary>
public enum DeliveryCycleOutcome
{
    /// <summary>Nothing was claimable. The loop sleeps before trying again.</summary>
    Idle = 0,

    /// <summary>An item was claimed, dispatched, and its outcome recorded.</summary>
    Dispatched = 1,

    /// <summary>
    /// The port threw instead of reporting per-recipient outcomes. Recorded as a temporary failure.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Dispatched"/> only so an operator can see transport faults in the
    /// counts. It is <em>not</em> a worse classification: an exception says nothing about which
    /// recipients were tried, so retrying is the only safe reading.
    /// </remarks>
    PortFaulted = 2,

    /// <summary>
    /// The item's metadata exists but its payload does not. Nothing was delivered and nothing was
    /// settled, an integrity fault for a human, not a delivery problem.
    /// </summary>
    PayloadMissing = 3,
}

/// <summary>The result of one delivery cycle.</summary>
public sealed record DeliveryCycleResult
{
    public required DeliveryCycleOutcome Outcome { get; init; }

    public string? QueueId { get; init; }

    public int RecipientsAttempted { get; init; }

    public string? Detail { get; init; }

    public static readonly DeliveryCycleResult Idle = new() { Outcome = DeliveryCycleOutcome.Idle };
}

/// <summary>Tunables for <see cref="QueueDeliveryWorker"/>.</summary>
public sealed record QueueDeliveryWorkerOptions
{
    /// <summary>
    /// Identity this worker leases under.
    /// </summary>
    /// <remarks>
    /// Unique per instance by default. Leases are attributed to it, so a stable, distinguishable
    /// value makes "which worker died holding this?" answerable from the history.
    /// </remarks>
    public string WorkerId { get; init; } = "worker-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>How long to sleep when the queue is empty before looking again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often to run the crash-recovery sweep.</summary>
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an in-flight delivery may run past shutdown before it is cut off.
    /// </summary>
    /// <remarks>
    /// <b>Drain is bounded, not unlimited.</b> Abandoning a delivery mid-flight risks a duplicate
    /// (the upstream may have accepted it), but waiting forever on a wedged connection means the
    /// process never exits. Past this window the delivery is cancelled and the lease is left to
    /// expire, so recovery reclaims it with the ambiguity recorded.
    /// </remarks>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The delivery worker: leases accepted items, dispatches them per recipient, and records outcomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never opens a socket.</b> Everything external happens behind <see cref="IDeliveryPort"/>,
/// so the queue stays ignorant of SMTP and a transport can be swapped without touching durability.
/// </para>
/// <para>
/// <b>One item at a time per worker.</b> Concurrency comes from running several workers, each with
/// its own <see cref="QueueDeliveryWorkerOptions.WorkerId"/>, rather than from threads inside one.
/// The lease is what makes that safe, and a worker that dispatched several items at once would be
/// leasing them all while holding them all in memory.
/// </para>
/// <para>
/// <b>Backoff and expiry are not the worker's job.</b> The worker records what happened; the store
/// decides when the next attempt is due, whether a recipient has run out of attempts, and whether
/// the message's lifetime has passed. A worker that scheduled its own retries would be a second,
/// divergent copy of that policy.
/// </para>
/// </remarks>
public sealed class QueueDeliveryWorker
{
    private readonly QueueStore _store;
    private readonly IDeliveryPort _port;
    private readonly QueueOptions _queueOptions;
    private readonly QueueDeliveryWorkerOptions _workerOptions;

    public QueueDeliveryWorker(
        QueueStore store,
        IDeliveryPort port,
        QueueOptions queueOptions,
        QueueDeliveryWorkerOptions? workerOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _queueOptions = queueOptions ?? throw new ArgumentNullException(nameof(queueOptions));
        _workerOptions = workerOptions ?? new QueueDeliveryWorkerOptions();

        if (_workerOptions.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerOptions), _workerOptions.PollInterval, "PollInterval must be positive.");
        }

        if (_workerOptions.DrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerOptions), _workerOptions.DrainTimeout, "DrainTimeout must be positive.");
        }
    }

    public string WorkerId => _workerOptions.WorkerId;

    /// <summary>
    /// Claims and dispatches at most one item.
    /// </summary>
    /// <remarks>
    /// The unit of work, split out so the interesting behaviour, which outcome lands on which
    /// recipient, can be driven directly, rather than observed through a running loop.
    /// </remarks>
    public async Task<DeliveryCycleResult> RunOnceAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var lease = await _store.ClaimNextAsync(_workerOptions.WorkerId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (lease is null)
        {
            return DeliveryCycleResult.Idle;
        }

        // A delivery with no pending recipients should be impossible, the item would not have been
        // claimable, but completing it is the safe response if the invariant is ever broken:
        // leaving it leased would strand it until the lease expired.
        if (lease.PendingRecipients.Count == 0)
        {
            await _store.CompleteAsync(lease, new DeliveryReport
            {
                WorkerId = _workerOptions.WorkerId,
                Recipients = [],
            }, CancellationToken.None).ConfigureAwait(false);

            return new DeliveryCycleResult
            {
                Outcome = DeliveryCycleOutcome.Dispatched,
                QueueId = lease.QueueId,
                Detail = "Claimed with no pending recipients; re-rolled rather than dispatched.",
            };
        }

        ReadOnlyMemory<byte> payload;
        try
        {
            payload = await ReadPayloadAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueIntegrityException integrity)
        {
            // Payload-before-metadata ordering makes this unreachable by design, so it is not a
            // delivery failure to retry, it is mail we accepted and cannot produce. The lease is
            // deliberately NOT completed: settling the item would erase the only signal, and the
            // recipient state would claim an outcome nobody observed. Recovery reports it.
            return new DeliveryCycleResult
            {
                Outcome = DeliveryCycleOutcome.PayloadMissing,
                QueueId = lease.QueueId,
                Detail = integrity.Message,
            };
        }

        var request = BuildRequest(lease, payload);

        DeliveryPortResult portResult;
        try
        {
            portResult = await _port.DeliverAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our own drain window closed, or the host is shutting down. What the transport managed
            // to do is unknown, so nothing is recorded and the lease is left to expire, recovery
            // reclaims it with the ambiguity visible rather than inventing an outcome.
            throw;
        }
        catch (Exception ex)
        {
            return await RecordPortFaultAsync(lease, ex).ConfigureAwait(false);
        }

        // None: recording what happened matters more than a prompt shutdown, and this is a local
        // write. A cancelled completion would lose the only record of an attempt that did happen.
        await _store.CompleteAsync(lease, portResult.AsReport(_workerOptions.WorkerId), CancellationToken.None)
            .ConfigureAwait(false);

        return new DeliveryCycleResult
        {
            Outcome = DeliveryCycleOutcome.Dispatched,
            QueueId = lease.QueueId,
            RecipientsAttempted = lease.PendingRecipients.Count,
            Detail = portResult.Detail,
        };
    }

    /// <summary>
    /// Runs the delivery loop until shutdown, then drains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On shutdown the loop stops claiming immediately, but an in-flight delivery is given
    /// <see cref="QueueDeliveryWorkerOptions.DrainTimeout"/> to finish. Cutting a delivery off the
    /// instant shutdown is requested would abandon it mid-flight, and the upstream may already have
    /// accepted the message, a duplicate created by our own shutdown.
    /// </para>
    /// <para>
    /// If the window closes first, the delivery is cancelled and its lease is left to expire. That
    /// is the bounded cost of not hanging forever on a wedged connection.
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken shutdownToken = default)
    {
        using var inFlight = new CancellationTokenSource();
        ITimer? drainTimer = null;

        await using var registration = shutdownToken.Register(() =>
        {
            // Not cancelling immediately: the delivery gets its window. That window is the whole
            // point of a bounded drain.
            //
            // Scheduled through the injected TimeProvider, like every other deadline in this
            // component. `CancelAfter(TimeSpan)` has no TimeProvider overload, so it reads the wall
            // clock directly, which would make this the one timing decision a deployment cannot
            // control and a test cannot drive.
            drainTimer = _queueOptions.TimeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                inFlight,
                _workerOptions.DrainTimeout,
                Timeout.InfiniteTimeSpan);
        }).ConfigureAwait(false);

        var nextRecovery = _queueOptions.TimeProvider.GetUtcNow();

        try
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                if (_queueOptions.TimeProvider.GetUtcNow() >= nextRecovery)
                {
                    await _store.RecoverAsync(cancellationToken: inFlight.Token).ConfigureAwait(false);
                    nextRecovery = _queueOptions.TimeProvider.GetUtcNow() + _workerOptions.RecoveryInterval;
                }

                var result = await RunOnceAsync(cancellationToken: inFlight.Token).ConfigureAwait(false);

                if (result.Outcome == DeliveryCycleOutcome.Idle)
                {
                    await Task.Delay(_workerOptions.PollInterval, _queueOptions.TimeProvider, shutdownToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, by either token. Falling through lets the loop exit normally rather than
            // surfacing cancellation as a failure of the worker.
        }
        finally
        {
            drainTimer?.Dispose();
            inFlight.Cancel();
        }
    }

    private async Task<DeliveryCycleResult> RecordPortFaultAsync(QueueLease lease, Exception exception)
    {
        // The port threw instead of reporting per-recipient outcomes, so we know only that something
        // unexpected happened, not which recipients were tried, and not whether anything was
        // accepted. Retrying is the safe reading: a duplicate is recoverable and a silent loss is
        // not. The detail says plainly that the outcome is unverified rather than dressing the
        // exception up as a delivery result we actually observed.
        var report = new DeliveryReport
        {
            WorkerId = _workerOptions.WorkerId,
            Detail = $"Transport threw {exception.GetType().Name}.",
            Recipients =
            [
                .. lease.PendingRecipients.Select(recipient => new RecipientDeliveryResult
                {
                    Recipient = recipient.Recipient,
                    Outcome = DeliveryAttemptOutcome.TemporaryFailure,
                    Detail =
                        $"Transport threw {exception.GetType().Name} before reporting a per-recipient " +
                        "outcome: whether this recipient was delivered is unknown. Treated as temporary " +
                        "so the message is retried rather than assumed lost.",
                }),
            ],
        };

        await _store.CompleteAsync(lease, report, CancellationToken.None).ConfigureAwait(false);

        return new DeliveryCycleResult
        {
            Outcome = DeliveryCycleOutcome.PortFaulted,
            QueueId = lease.QueueId,
            RecipientsAttempted = lease.PendingRecipients.Count,
            Detail = exception.Message,
        };
    }

    private async Task<ReadOnlyMemory<byte>> ReadPayloadAsync(QueueLease lease, CancellationToken cancellationToken)
    {
        await using var stream = _store.OpenPayload(lease.Item);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(lease.Item.PayloadBytes, int.MaxValue));

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static DeliveryRequest BuildRequest(QueueLease lease, ReadOnlyMemory<byte> payload)
    {
        var envelope = lease.Item.Envelope;

        return new DeliveryRequest
        {
            QueueId = lease.QueueId,
            InternalMessageId = envelope.InternalMessageId,
            TenantId = envelope.TenantId,
            Direction = envelope.Direction,
            TrustedPrincipalId = envelope.TrustedPrincipalId,
            MailFrom = envelope.MailFrom,

            // Exactly the due recipients: a recipient already delivered, or one still inside its own
            // backoff, must not be sent to again.
            Recipients = [.. lease.PendingRecipients.Select(r => r.Recipient)],

            Payload = payload,
            ExpiresAt = lease.Item.ExpiresAt,
            UntrustedMessageIdHeader = envelope.UntrustedMessageIdHeader,
        };
    }
}
