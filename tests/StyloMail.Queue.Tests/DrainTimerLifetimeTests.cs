using StyloMail.Core;
using StyloMail.Queue;

namespace StyloMail.Queue.Tests;

/// <summary>
/// A clock that records every timer the worker creates, so a test can ask what the drain timer's
/// lifetime actually was instead of inferring it from a crash.
/// </summary>
/// <remarks>
/// <para>
/// The recorded timers delegate to real timers, so a test that watches them still gets the real
/// timing behaviour: recording is observation, not a simulation.
/// </para>
/// <para>
/// It can also <em>hold</em> the creation of a timer whose state is a <see cref="CancellationTokenSource"/>,
/// which is the worker's drain timer and nothing else (the loop's poll delay is a <c>Task.Delay</c>
/// with its own promise as the state). That hold is how a test reproduces, deterministically, the
/// window the Host suite reaches on its own: the shutdown callback is inside its timer creation, and
/// therefore has not yet assigned the timer it is creating, while the worker's loop is free to read
/// that field as null and return.
/// </para>
/// </remarks>
internal sealed class RecordingClock : TimeProvider
{
    private readonly List<RecordingTimer> _timers = [];
    private readonly ManualResetEventSlim _drainTimerGate = new(initialState: true);
    private readonly ManualResetEventSlim _holdingDrainTimerCreation = new(initialState: false);

    public IReadOnlyList<RecordingTimer> Timers
    {
        get { lock (_timers) { return [.. _timers]; } }
    }

    /// <summary>The timers whose state is the drain source.</summary>
    public IReadOnlyList<RecordingTimer> DrainTimers =>
        [.. Timers.Where(t => t.State is CancellationTokenSource)];

    /// <summary>Holds the next drain-timer creation, so the creating thread cannot finish it yet.</summary>
    public void HoldDrainTimerCreation() => _drainTimerGate.Reset();

    /// <summary>Lets a held drain-timer creation complete.</summary>
    public void ReleaseDrainTimerCreation() => _drainTimerGate.Set();

    /// <summary>
    /// Whether something is now held inside that creation. False is an answer, not a failure: a
    /// worker whose timer is created up front has nothing to hold.
    /// </summary>
    public bool WaitUntilHoldingDrainTimerCreation(TimeSpan timeout) =>
        _holdingDrainTimerCreation.Wait(timeout);

    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (state is CancellationTokenSource)
        {
            _holdingDrainTimerCreation.Set();
            _drainTimerGate.Wait(TimeSpan.FromSeconds(15));
        }

        var timer = new RecordingTimer(callback, state, dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }
}

/// <summary>One recorded timer: what it was armed with, whether it was disposed, and its callback.</summary>
internal sealed class RecordingTimer : ITimer
{
    private readonly ITimer _inner;

    public RecordingTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Callback = callback;
        State = state;
        DueTime = dueTime;
        _inner = TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    public TimerCallback Callback { get; }

    public object? State { get; }

    /// <summary>What the timer was last armed with. <see cref="Timeout.InfiniteTimeSpan"/> is unarmed.</summary>
    public TimeSpan DueTime { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Whether this timer can still fire into the thread pool.</summary>
    public bool Armed => !Disposed && DueTime != Timeout.InfiniteTimeSpan;

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        DueTime = dueTime;
        return _inner.Change(dueTime, period);
    }

    public void Dispose()
    {
        Disposed = true;
        _inner.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return _inner.DisposeAsync();
    }
}

/// <summary>
/// The drain timer's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shutdown callback can still be running when <see cref="QueueDeliveryWorker.RunAsync"/>
/// returns, and a timer it creates there is a timer pointed at a source the worker has already
/// disposed.</b> This is observed, not theorised. One instrumented full Host suite run recorded, for
/// two workers in the same run, <c>finally: drainTimer=NULL</c> and then, in the same millisecond,
/// <c>shutdown callback: afterReturn=True</c> and <c>created drain timer armed=00:00:30</c>; exactly
/// 30.00s after the first of those the timer thread logged
/// <c>TIMER: CANCEL THREW ObjectDisposedException</c> and the test host aborted. The drain field the
/// callback assigns is captured in a closure, so an assignment that lands after the <c>finally</c>
/// has read it null is a timer nothing will ever dispose.
/// </para>
/// <para>
/// The window is reachable because <c>Cancel()</c> sets the cancelled flag before it runs any
/// callback: the worker's loop, whose poll delay is completed by that same cancellation, can see the
/// shutdown, finish its delivery, read the field as null and return while the cancelling thread is
/// still inside the callback. It is not rare either: over 4 full Host suite runs the suite aborted
/// twice (at 195/369 in 210s and at 74/369 in 59s) and completed twice (369/369, exit 0).
/// </para>
/// <para>
/// The tests below drive that ordering by hand, so a fix is measured instead of gambled on.
/// </para>
/// </remarks>
public class DrainTimerLifetimeTests
{
    [Fact]
    public async Task The_drain_timer_exists_unarmed_for_the_life_of_the_worker()
    {
        var clock = new RecordingClock();
        using var h = new QueueHarness(_ => new QueueOptions { TimeProvider = clock });
        await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var port = new FakeDeliveryPort(async (_, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = new QueueDeliveryWorker(h.Store, port, h.Options, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromSeconds(30),
        });

        using var shutdown = new CancellationTokenSource();
        var running = worker.RunAsync(shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The timer belongs to the worker, not to the shutdown: it exists, unarmed, while there is
        // nothing to drain, and shutdown only arms it. A timer created BY the shutdown callback is
        // created on a thread that can still be inside that callback after this method has returned,
        // which is the leak the next test pins down.
        var timer = Assert.Single(clock.DrainTimers);
        Assert.False(timer.Armed, "The drain timer was armed before any shutdown asked it to drain.");

        shutdown.Cancel();
        gate.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(timer.Disposed, "The drain timer outlived the worker that created it.");
    }

    [Fact]
    public async Task A_shutdown_callback_that_outruns_the_worker_leaves_no_armed_timer()
    {
        var clock = new RecordingClock();
        using var h = new QueueHarness(_ => new QueueOptions { TimeProvider = clock });
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var port = new FakeDeliveryPort(async (_, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = new QueueDeliveryWorker(h.Store, port, h.Options, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromSeconds(30),
        });

        using var shutdown = new CancellationTokenSource();
        var running = worker.RunAsync(shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Hold the cancelling thread inside a timer creation performed by the shutdown callback, which
        // is the microsecond window the Host suite falls into by itself: the callback has started, so
        // it is too late to be skipped, and it has not finished, so any field it assigns is still null.
        //
        // Read what this does and does not do. It forces that window only if the callback is the thing
        // creating the timer, which is exactly the defect. Against the fixed worker, which creates the
        // timer before the callback can run, the hold engages nothing: the latch below was already set
        // by that earlier creation, so the wait returns at once and this test degrades to a plain
        // regression check rather than a forced race. That is the intended shape, not a defect in the
        // test: the test's teeth are that it FAILS when the timer is created by the callback, which is
        // what the guard-only variant measured (2 failed of 3). It is not evidence that the race was
        // forced here.
        clock.HoldDrainTimerCreation();
        var cancelling = Task.Run(shutdown.Cancel);
        clock.WaitUntilHoldingDrainTimerCreation(TimeSpan.FromSeconds(5));
        gate.SetResult();
        await WorkerHasRecordedItsDeliveryAsync(h, queueId);

        // The worker's remaining steps after that write are the loop check and its finally, so this
        // is the point at which an unheld callback would already have lost the race.
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        clock.ReleaseDrainTimerCreation();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelling.WaitAsync(TimeSpan.FromSeconds(10));

        // A timer the shutdown callback created is a timer created against a source the worker has
        // disposed, armed for the full drain window, and disposed by nobody: it fires tens of seconds
        // later on a thread-pool thread, and Cancel() on a disposed source is an unhandled
        // ObjectDisposedException there, which aborts the process. That is the Host suite printing
        // "Passed! - Failed: 0" and then "Test Run Aborted."
        var armed = clock.DrainTimers.Where(t => t.Armed).ToList();
        Assert.True(
            armed.Count == 0,
            $"{armed.Count} drain timer(s) are still armed after RunAsync returned: "
            + string.Join(
                ", ",
                armed.Select(t => $"armed for {t.DueTime}, state={(t.State as CancellationTokenSource)?.IsCancellationRequested}")));
    }

    [Fact]
    public async Task A_delivery_cut_off_by_the_drain_window_leaves_no_armed_timer()
    {
        var clock = new RecordingClock();
        using var h = new QueueHarness(_ => new QueueOptions { TimeProvider = clock });
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var entered = new TaskCompletionSource();
        var port = new FakeDeliveryPort(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeDeliveryPort.Delivered("rcpt@example.test");
        });

        var worker = new QueueDeliveryWorker(h.Store, port, h.Options, new QueueDeliveryWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            DrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        using var shutdown = new CancellationTokenSource();
        var running = worker.RunAsync(shutdown.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Shutdown mid-flight, and let the drain window be what ends the delivery: the loop exits
        // through the cancellation this timer caused, which is the path where the callback and this
        // method's teardown are closest together.
        shutdown.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        // The assertion with teeth. A delivery the drain window cut off mid-flight was not observed by
        // anyone, so nothing may be recorded for it: the recipient stays unsettled and the lease is
        // left to expire, for recovery to reclaim with the ambiguity visible. This can fail, and it is
        // the invariant most likely to be broken by a future "just record InDoubt on cancel" change,
        // which this lane has already ruled against once.
        var item = await h.Store.GetItemAsync(queueId);
        Assert.NotNull(item);
        var recipient = QueueHarness.By(item, "rcpt@example.test");
        Assert.True(
            recipient.State is not (DeliveryState.Delivered or DeliveryState.TerminalFailure),
            $"A delivery cut off by the drain window was recorded as {recipient.State}, but nothing "
            + "observed an outcome: it must be left for recovery, not settled.");

        // And the lifetime assertion. Note honestly that this one cannot fail on its own: disposal is
        // guaranteed by the `using` on the timer, so `Armed` is false however the callback behaved. It
        // is kept as documentation of the intended lifetime, not as evidence of it; the test above it
        // is the one that can fail, and the timer's real proof is its absence under the negative
        // control.
        var timer = Assert.Single(clock.DrainTimers);
        Assert.False(timer.Armed, "The drain timer was still armed after the worker returned.");
    }

    /// <summary>Waits until the worker has written the delivery it was in the middle of.</summary>
    private static async Task WorkerHasRecordedItsDeliveryAsync(QueueHarness h, string queueId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var item = await h.Store.GetItemAsync(queueId);
            if (item is not null
                && QueueHarness.By(item, "rcpt@example.test").State == DeliveryState.Delivered)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException(
            "The delivery was never recorded, so the ordering this test needs was not reached.");
    }
}
