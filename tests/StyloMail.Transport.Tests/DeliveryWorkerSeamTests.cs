using StyloMail.Core;
using StyloMail.Persistence;
using StyloMail.Queue;
using StyloMail.Transport.Delivery;
using StyloMail.Transport.Tests.Support;

namespace StyloMail.Transport.Tests;

/// <summary>
/// The seam: the real <see cref="SmtpDeliveryPort"/> driven by the real <see cref="QueueDeliveryWorker"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Both lanes were green independently and nothing had ever run one
/// against the other. The port contract is new and only its author had tested it, so mine exercised
/// <em>my</em> reading of the interface against fakes I wrote to that reading, and theirs exercised
/// theirs. Two readings of one interface agree by inspection; only execution says whether they are
/// the same reading. These are the failure modes worth finding — an <c>InDoubt</c> that settles, a
/// partial delivery that reads as a full success.
/// </para>
/// <para>
/// Written by <c>queue-</c> (the consumer side, which owns what the contract means); the rig is
/// <c>transport-</c>'s. It lives here rather than in a third project because this project already
/// references the queue, so the coupling is already paid either way.
/// </para>
/// <para>
/// <b>Not covered here:</b> a port that <em>throws</em>. The real port returns per-recipient outcomes
/// for every failure including a closed socket — that is the contract — so a throwing port has to be
/// a hand-written stub, and it is covered in <c>StyloMail.Queue.Tests</c> where the worker's handling
/// of it belongs.
/// </para>
/// </remarks>
public class DeliveryWorkerSeamTests
{
    [Fact]
    public async Task An_in_doubt_delivery_from_the_real_port_does_not_settle_the_recipient()
    {
        // The upstream accepts the body and the end-of-data terminator, then drops the connection
        // before answering. The message is fully on the wire and unanswered — it may already be
        // accepted. This is the ambiguity the whole component exists to preserve.
        var behaviour = new FakeSmtpBehaviour { DropAfterDataTerminator = true };
        await using var server = FakeSmtpServer.Start(behaviour);
        await using var fixture = new SeamFixture(server);

        var queueId = await fixture.AcceptAsync("rcpt@example.test");

        var result = await fixture.Worker.RunOnceAsync();

        Assert.Equal(DeliveryCycleOutcome.Dispatched, result.Outcome);

        var recipient = fixture.Recipient(queueId, "rcpt@example.test");

        // Retried, not settled, and not terminally failed — the port's InDoubt reached the queue
        // intact. If the two readings of the contract had diverged, this is where it shows.
        Assert.Equal(DeliveryState.RetryScheduled, recipient.State);
        Assert.Null(recipient.DeliveredAt);

        var attempt = Assert.Single(await fixture.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous, "A lost acknowledgement must stay flagged as ambiguous.");

        // It consumed an attempt: retrying is a real cost, not a free unwind.
        Assert.Equal(1, recipient.Attempts);

        // And it really did reach the wire — otherwise this asserts nothing.
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task A_partial_delivery_from_the_real_port_never_reads_as_a_full_success()
    {
        // One recipient refused with a 550, two accepted.
        var behaviour = new FakeSmtpBehaviour();
        behaviour.RcptCodesByRecipient["c@example.test"] = 550;
        await using var server = FakeSmtpServer.Start(behaviour);
        await using var fixture = new SeamFixture(server);

        var queueId = await fixture.AcceptAsync("a@example.test", "b@example.test", "c@example.test");

        await fixture.Worker.RunOnceAsync();

        var item = await fixture.Store.GetItemAsync(queueId);

        // The split the spec insists must never be collapsed: two delivered, one failed, and the
        // message is neither "sent" nor "failed".
        Assert.Equal(QueueItemOutcome.PartiallyDelivered, item!.Outcome);
        Assert.Equal(DeliveryState.Delivered, SeamFixture.By(item, "a@example.test").State);
        Assert.Equal(DeliveryState.Delivered, SeamFixture.By(item, "b@example.test").State);
        Assert.Equal(DeliveryState.TerminalFailure, SeamFixture.By(item, "c@example.test").State);

        // The permanently failed recipient is not retried, and the delivered ones are not resent.
        Assert.DoesNotContain(item.Recipients, r => r.IsWorkable);
    }

    [Fact]
    public async Task A_cancellation_landing_after_the_terminator_surfaces_as_in_doubt()
    {
        // The upstream consumes the body and its terminator, then waits before giving a verdict. That
        // is the only state from which a *cancellation* — rather than a connection loss — can land
        // after the terminator, which is the case where the message may already be accepted.
        //
        // **The 30s delay is a ceiling, never waited out.** The worker's drain window closes long
        // before it, so the cancellation is guaranteed to land inside the window rather than racing
        // it. transport-'s framing: a window, not a race. The whole test runs in ~300ms.
        var behaviour = new FakeSmtpBehaviour { FinalReplyDelay = TimeSpan.FromSeconds(30) };
        await using var server = FakeSmtpServer.Start(behaviour);
        await using var fixture = new SeamFixture(server);

        var queueId = await fixture.AcceptAsync("rcpt@example.test");

        var worker = fixture.WorkerFor(new QueueDeliveryWorkerOptions
        {
            WorkerId = "seam-drain",
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromMilliseconds(300),
        });

        using var shutdown = new CancellationTokenSource();
        var running = Task.Run(() => worker.RunAsync(shutdown.Token));

        // A real synchronisation point, not a sleep-to-guess: the server has consumed the body and
        // the terminator, so the terminator is written and the session is sitting in its delay.
        await server.WaitForMessagesAsync(1, TimeSpan.FromSeconds(10));

        shutdown.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(15));

        // The upstream may hold the message, so the recipient is retried with the ambiguity recorded
        // rather than settled — and crucially *not* settled as a plain failure because our own drain
        // window closed. That distinction is the whole point of the port classifying per recipient:
        // the worker cannot know how far the protocol got, and must not guess.
        var recipient = fixture.Recipient(queueId, "rcpt@example.test");
        Assert.Equal(DeliveryState.RetryScheduled, recipient.State);
        Assert.Null(recipient.DeliveredAt);
        Assert.Equal(1, recipient.Attempts);

        var attempt = Assert.Single(await fixture.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous);
    }

    // RESOLVED AND REMOVED — and the resolution was neither of the two readings offered, which is
    // why it was worth raising rather than guessing.
    //
    // The assertion was wrong, and so was the rig's knob name. `DropDuringDataBody` was not dropping
    // during the body: the fake replied to DATA and closed without reading, so what the client
    // observed depended on whether its body write completed into the kernel socket buffer. Measured
    // on loopback: <= 4 KB is written successfully and the failure is seen awaiting the verdict
    // (terminator written -> in-doubt, and honestly so); >= 64 KB overflows the buffer so the write
    // itself fails (terminator never written -> plain temporary failure). The threshold is the
    // socket buffer, not the knob.
    //
    // Both outcomes are correct, so there was no contradiction to resolve — only a fixture whose
    // name promised an interruption it could not deliver. The knob is now `CloseAfterDataCommand`
    // and says what it does, and both sides of the boundary are pinned in `SmtpSessionTests`:
    // `AConnectionLostBeforeTheTerminatorIsReached_IsAFailureNotInDoubt` (4 MB, deliberately) and
    // `AConnectionLostAfterASmallBodyIsInDoubt_BecauseTheTerminatorDidGetWritten`.
    //
    // Removed rather than weakened, per the instruction in its own remarks. The two tests above are
    // the coverage; this one asserted a case the fixture could not produce.

}

/// <summary>
/// A real <see cref="QueueStore"/> and worker wired to the real port.
/// </summary>
/// <remarks>
/// Deliberately not shared with <c>StyloMail.Queue.Tests</c>: that harness injects a fake port, and
/// the whole point here is that nothing about delivery is faked. Kept minimal so the test says what
/// it does.
/// </remarks>
internal sealed class SeamFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly SmtpDeliveryPort _port;

    public SeamFixture(FakeSmtpServer server)
    {
        _root = Path.Combine(Path.GetTempPath(), "stylomail-seam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Clock = new SeamClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Options = new QueueOptions { TimeProvider = Clock };

        Store = new QueueStore(
            new SqliteConnectionFactory(Path.Combine(_root, "queue.db")),
            new SpoolStore(Path.Combine(_root, "spool")),
            Options);

        _port = new SmtpDeliveryPort(
            SmtpTestRig.PlainUpstream(server),
            Clock,
            certificateValidation: SmtpTestRig.TrustAnyCertificate);

        Worker = new QueueDeliveryWorker(Store, _port, Options, new QueueDeliveryWorkerOptions
        {
            WorkerId = "seam-worker",
            PollInterval = TimeSpan.FromMilliseconds(10),
        });
    }

    public SeamClock Clock { get; }

    public QueueOptions Options { get; }

    public QueueStore Store { get; }

    public QueueDeliveryWorker Worker { get; }

    /// <summary>A worker over the same store and port, with different options.</summary>
    /// <remarks>
    /// Needed because the drain-window case has to close the window quickly — it is a ceiling the
    /// test must reach in hundreds of milliseconds, not a wait it sits through.
    /// </remarks>
    public QueueDeliveryWorker WorkerFor(QueueDeliveryWorkerOptions options)
        => new(Store, _port, Options, options);

    public async Task<string> AcceptAsync(params string[] recipients)
    {
        var result = await Store.AcceptAsync(new QueueSubmission
        {
            TenantId = "acme",
            InternalMessageId = "msg-" + Guid.NewGuid().ToString("N"),
            Direction = MailDirection.Outbound,
            TrustedPrincipalId = "principal-1",
            MailFrom = "sender@acme.test",
            MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
            Payload = SmtpTestRig.CanonicalMessage(),
            Recipients = [.. recipients.Select(r => new RecipientAdmission { Recipient = r })],
        });

        Assert.True(result.IsAccepted, $"{result.Admission}: {result.Detail}");
        return result.QueueId!;
    }

    public QueueRecipientState Recipient(string queueId, string recipient)
    {
        var item = Store.GetItemAsync(queueId).GetAwaiter().GetResult();
        Assert.NotNull(item);
        return By(item!, recipient);
    }

    public static QueueRecipientState By(QueueItem item, string recipient)
        => item.Recipients.Single(r => string.Equals(r.Recipient, recipient, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        await _port.DisposeAsync();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A clock the test drives by hand, so nothing here waits on real time.</summary>
internal sealed class SeamClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
