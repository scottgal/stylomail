namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// A cancellation token that expires after a delay measured on an injected clock.
/// </summary>
/// <remarks>
/// <b>Everything timeout-shaped goes through here, and here goes through <see cref="TimeProvider"/>.</b>
/// The alternative, <c>Task.Delay(timeout)</c>, is untestable in the way that matters: a test for
/// "an unauthenticated client is disconnected after the bound" would have to actually wait out the
/// bound, so in practice it either does not get written or gets written with a bound small enough to
/// be meaningless. With an injected clock the test advances a fake timer and the real bound is
/// exercised.
///
/// <para>
/// This is the same reasoning the transport applies to its own timeouts, and the same one
/// <c>AssessmentContext</c> states for the whole system: an injected clock is what makes replay and
/// boundary conditions reproducible rather than approximate.
/// </para>
///
/// <para>
/// Two sources are retained and disposed together: the timer that produces the deadline, and the
/// linked source that combines it with the caller's token. Disposing only one would leave the other
/// registered against the caller's token for the lifetime of the session.
/// </para>
/// </remarks>
internal sealed class TimeoutScope : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;

    internal TimeoutScope(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _deadline = new CancellationTokenSource(timeout, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
    }

    /// <summary>Cancelled when the caller cancels or the deadline passes.</summary>
    internal CancellationToken Token => _linked.Token;

    /// <summary>
    /// True only when the deadline itself elapsed, as distinct from the scope being cancelled from
    /// this side.
    /// </summary>
    /// <remarks>
    /// The linked token cannot answer this: it is cancelled in both cases, so asking it would report
    /// every deliberate shutdown as a timeout. The distinction is what lets a session report "the
    /// client finished" and "we had to reclaim this session" as the different outcomes they are.
    /// </remarks>
    internal bool DeadlineElapsed => _deadline.IsCancellationRequested;

    /// <summary>
    /// Cancels the scope from this side, ahead of the deadline.
    /// </summary>
    /// <remarks>
    /// Used by the relay: when one direction ends, the other has to be stopped rather than waited
    /// on, because a peer that has gone away will never close its own half.
    /// </remarks>
    internal void Cancel() => _linked.Cancel();

    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
    }
}
