namespace StyloMail.Nimble;

/// <summary>
/// Trips after repeated failures so a server that is down or thrashing cannot consume the delivery
/// path's latency budget on every message.
/// </summary>
/// <remarks>
/// <para>
/// An open circuit makes semantic dimensions report <c>Unavailable</c>, an explicit state. It must
/// never be read downstream as "no risk found": a provider outage is not a clean bill of health.
/// </para>
/// <para>
/// <b>This is a copy of the hosted adapter's breaker, not a reference to it.</b> That one is internal
/// to its assembly, and the alternative to duplicating twenty lines here would be to move a shared
/// breaker into Core, which is not this lane's file to change. The duplication is deliberate and
/// bounded: the type is small, it has its own tests, and it depends on nothing but a clock.
/// </para>
/// <para>
/// It is a copy in behaviour too, and one difference is worth naming: this breaker sees more failures
/// than the hosted one does, because a deadline here counts as a failure rather than being raised.
/// </para>
/// </remarks>
internal sealed class NimbleCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public NimbleCircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _time = time;
    }

    /// <summary>
    /// True while calls should be skipped. Once the open duration elapses the circuit allows a single
    /// probe through, so recovery is discovered rather than waited out.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (_openedAt is null)
                {
                    return false;
                }

                if (_time.GetUtcNow() - _openedAt.Value >= _openDuration)
                {
                    _openedAt = null;
                    _consecutiveFailures = 0;
                    return false;
                }

                return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _openedAt = _time.GetUtcNow();
            }
        }
    }
}
