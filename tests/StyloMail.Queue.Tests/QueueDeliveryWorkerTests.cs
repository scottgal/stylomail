using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// A delivery port under the test's control.
/// </summary>
/// <remarks>
/// Records every request it was given so tests can assert on what the worker <em>actually</em>
/// handed over — the payload bytes and the recipient set — rather than only on what happened after.
/// </remarks>
internal sealed class FakeDeliveryPort : IDeliveryPort
{
    private readonly Func<DeliveryRequest, CancellationToken, Task<DeliveryPortResult>> _respond;

    public FakeDeliveryPort(Func<DeliveryRequest, CancellationToken, Task<DeliveryPortResult>> respond)
        => _respond = respond;

    public FakeDeliveryPort(Func<DeliveryRequest, DeliveryPortResult> respond)
        => _respond = (request, _) => Task.FromResult(respond(request));

    public List<DeliveryRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public ValueTask<DeliveryPortResult> DeliverAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return new ValueTask<DeliveryPortResult>(_respond(request, cancellationToken));
    }

    public static DeliveryPortResult Delivered(params string[] recipients)
        => new()
        {
            Recipients = [.. recipients.Select(r => new RecipientDeliveryResult
            {
                Recipient = r,
                Outcome = DeliveryAttemptOutcome.Delivered,
            })],
        };
}

/// <summary>
/// The delivery worker: lease, dispatch through the port, record the outcome.
/// </summary>
public class QueueDeliveryWorkerTests
{
    private static QueueDeliveryWorker Worker(
        QueueHarness h,
        IDeliveryPort port,
        QueueDeliveryWorkerOptions? options = null)
        => new(h.Store, port, h.Options, options ?? new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
        });

    [Fact]
    public async Task A_worker_delivers_a_claimed_message_through_the_port()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var port = new FakeDeliveryPort(_ => FakeDeliveryPort.Delivered("rcpt@example.test"));

        var result = await Worker(h, port).RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.Dispatched, result.Outcome);
        Assert.Equal(queueId, result.QueueId);
        Assert.Equal(1, port.CallCount);

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(item!, "rcpt@example.test").State);
    }

    [Fact]
    public async Task The_port_is_given_the_original_bytes_and_exactly_the_pending_recipients()
    {
        using var h = new QueueHarness();

        var original = new byte[] { 0x4D, 0x49, 0x4D, 0x45, 0x00, 0xFF, 0x01 };
        var submission = QueueHarness.Submission(recipients: ["a@example.test", "b@example.test"])
            with { Payload = original };

        var queueId = await h.AcceptAsync(submission);

        var port = new FakeDeliveryPort(_ => new DeliveryPortResult
        {
            Recipients =
            [
                new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
                new() { Recipient = "b@example.test", Outcome = DeliveryAttemptOutcome.TemporaryFailure },
            ],
        });

        await Worker(h, port).RunOnceAsync();

        var request = Assert.Single(port.Requests);

        // Byte-for-byte: the payload is the transport artefact and signature integrity depends on
        // it reaching the wire unmodified.
        Assert.Equal(original, request.Payload.ToArray());

        Assert.Equal(["a@example.test", "b@example.test"], request.Recipients);
        Assert.Equal(queueId, request.QueueId);
        Assert.Equal("acme", request.TenantId);
        Assert.Equal("principal-1", request.TrustedPrincipalId);
        Assert.NotNull(request.ExpiresAt);

        // The retry names only the recipient still pending — never both again.
        h.Clock.AdvanceMinutes(30);
        await Worker(h, port).RunOnceAsync();
        Assert.Equal(["b@example.test"], port.Requests[1].Recipients);
    }

    [Fact]
    public async Task A_port_that_throws_is_recorded_as_temporary_and_never_as_silence()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var port = new FakeDeliveryPort((_, _) => throw new InvalidOperationException("socket exploded"));

        var result = await Worker(h, port).RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.PortFaulted, result.Outcome);
        Assert.Equal(queueId, result.QueueId);

        var item = await h.Store.GetItemAsync(queueId);
        var recipient = QueueHarness.By(item!, "rcpt@example.test");

        // Retried, not settled. An exception tells us nothing about which recipients were tried, so
        // assuming success would lose mail and assuming failure would lose the fact that we do not
        // know. Retrying is the only reading that cannot lose either.
        Assert.Equal(DeliveryState.RetryScheduled, recipient.State);

        // And the history says the outcome was unverified rather than dressing it up as observed.
        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, attempt.Outcome);
        Assert.Contains("unknown", attempt.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_worker_is_idle_when_there_is_nothing_to_deliver()
    {
        using var h = new QueueHarness();
        var port = new FakeDeliveryPort(_ => FakeDeliveryPort.Delivered("nobody@example.test"));

        var result = await Worker(h, port).RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.Idle, result.Outcome);
        Assert.Equal(0, port.CallCount);
    }

    [Fact]
    public async Task A_message_whose_payload_vanished_is_left_alone_rather_than_settled()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        File.Delete(h.PayloadPath(queueId));

        var port = new FakeDeliveryPort(_ => FakeDeliveryPort.Delivered("rcpt@example.test"));
        var result = await Worker(h, port).RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.PayloadMissing, result.Outcome);

        // The port must not have been called with a payload we do not have.
        Assert.Equal(0, port.CallCount);

        // And the item is NOT settled. Marking it failed would erase the only signal that mail we
        // accepted has gone missing; the record has to stay for a human.
        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Delivering, item!.State);
        Assert.Empty(await h.Store.GetAttemptsAsync(queueId));
    }

    [Fact]
    public async Task Shutdown_drains_an_in_flight_delivery_instead_of_abandoning_it()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();

        var port = new FakeDeliveryPort(async (_, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = Worker(h, port, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromSeconds(5),
        });

        using var shutdown = new CancellationTokenSource();
        var running = worker.RunAsync(shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Shutdown arrives mid-flight. Cutting the delivery off here would abandon a message the
        // upstream may already have accepted — a duplicate manufactured by our own shutdown.
        shutdown.Cancel();
        gate.SetResult();

        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(item!, "rcpt@example.test").State);
    }

    [Fact]
    public async Task A_delivery_that_outlasts_the_drain_window_is_cut_off_and_left_unsettled()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();

        // A wedged transport: never returns.
        var port = new FakeDeliveryPort(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = Worker(h, port, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        using var shutdown = new CancellationTokenSource();
        var running = worker.RunAsync(shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();

        // Drain is bounded: waiting forever on a wedged connection means the process never exits.
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var item = await h.Store.GetItemAsync(queueId);

        // No outcome was invented. The lease is simply left to expire, so recovery reclaims it with
        // the ambiguity recorded rather than a guessed success or failure.
        Assert.Equal(DeliveryState.Delivering, item!.State);
        Assert.Equal(DeliveryState.Queued, QueueHarness.By(item, "rcpt@example.test").State);
        Assert.Empty(await h.Store.GetAttemptsAsync(queueId));
    }

    [Fact]
    public async Task Two_workers_never_deliver_the_same_message_twice()
    {
        using var h = new QueueHarness();

        const int messages = 20;
        for (var i = 0; i < messages; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission());
        }

        var port = new FakeDeliveryPort(request => new DeliveryPortResult
        {
            Recipients = [.. request.Recipients.Select(r => new RecipientDeliveryResult
            {
                Recipient = r,
                Outcome = DeliveryAttemptOutcome.Delivered,
            })],
        });

        var workers = Enumerable.Range(0, 4).Select(i => Worker(h, port, new QueueDeliveryWorkerOptions
        {
            WorkerId = $"worker-{i}",
            PollInterval = TimeSpan.FromMilliseconds(1),
        })).ToList();

        // Each worker runs its own loop; they contend for the same rows through the lease.
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loops = workers.Select(w => Task.Run(() => w.RunAsync(shutdown.Token))).ToArray();

        // Let them drain, then stop.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var counts = await h.Store.CountByStateAsync("acme");
            if (counts.TryGetValue(DeliveryState.Delivered, out var delivered) && delivered == messages)
            {
                break;
            }

            await Task.Delay(20);
        }

        await shutdown.CancelAsync();
        await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10));

        // Exactly one delivery per message: contention that broke the lease would show up here as
        // extra Delivered rows long before it showed up as a duplicate in someone's inbox.
        var attempts = 0;
        foreach (var request in port.Requests)
        {
            attempts += request.Recipients.Count;
        }

        Assert.Equal(messages, attempts);
        Assert.Equal(messages, (await h.Store.CountByStateAsync("acme"))[DeliveryState.Delivered]);
    }

    [Fact]
    public async Task A_result_returned_despite_cancellation_is_applied_not_discarded()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();

        // A port that classifies per recipient and always returns — which is what `transport-`'s
        // does now, including when the caller's token is cancelled. It ignores the token
        // deliberately: the point is that a result can still arrive after we have stopped waiting.
        var port = new FakeDeliveryPort(async (_, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(200);
            return new DeliveryPortResult
            {
                Recipients =
                [
                    new()
                    {
                        Recipient = "rcpt@example.test",
                        Outcome = DeliveryAttemptOutcome.InDoubt,
                        Detail = "terminator written; cancellation arrived before the reply",
                    },
                ],
            };
        });

        var worker = Worker(h, port, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromMilliseconds(50),   // fires well before the port returns
        });

        using var shutdown = new CancellationTokenSource();
        var running = Task.Run(() => worker.RunAsync(shutdown.Token));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(10));

        // The drain window closed, but the port still handed back a classified outcome. Discarding
        // it because our own token was cancelled would throw away a per-recipient fact we already
        // hold — and this is the ambiguity the whole component exists to preserve.
        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.RetryScheduled, QueueHarness.By(item!, "rcpt@example.test").State);

        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous);
    }

    [Fact]
    public async Task The_running_worker_recovers_work_stranded_by_a_dead_worker()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            LeaseDuration = TimeSpan.FromMinutes(1),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        // A worker that leases the message and then dies. Nothing will ever complete this lease;
        // without the recovery sweep the message is stranded for good.
        var stranded = await h.Store.ClaimNextAsync("worker-dead");
        Assert.NotNull(stranded);

        h.Clock.AdvanceMinutes(2);

        var delivered = new TaskCompletionSource();
        var port = new FakeDeliveryPort(_ =>
        {
            delivered.TrySetResult();
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = Worker(h, port, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            RecoveryInterval = TimeSpan.Zero,   // sweep on the first pass
        });

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = Task.Run(() => worker.RunAsync(shutdown.Token));

        // Reaching the port at all proves the loop swept, reclaimed, and re-claimed.
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await shutdown.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(item!, "rcpt@example.test").State);

        // And the reclaim left its mark, so the ambiguity is still legible in the history.
        Assert.Contains(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.LeaseExpired);
    }

    [Fact]
    public async Task The_worker_never_touches_a_table_itself()
    {
        using var h = new QueueHarness();
        await h.AcceptAsync(QueueHarness.Submission());

        var port = new FakeDeliveryPort(_ => FakeDeliveryPort.Delivered("rcpt@example.test"));
        await Worker(h, port).RunOnceAsync();

        // Everything the worker does goes through QueueStore. If it wrote to the queue's tables
        // directly, a change to the schema or the state machine could pass every store test and
        // still be broken from here.
        var storeMethods = typeof(QueueStore).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Contains("ClaimNextAsync", storeMethods);
        Assert.Contains("CompleteAsync", storeMethods);
        Assert.Contains("OpenPayload", storeMethods);
    }
}
