namespace StyloMail.Jev;

/// <summary>
/// Trips after repeated provider failures so a struggling provider cannot consume the
/// delivery path's latency budget on every message.
/// </summary>
/// <remarks>
/// An open circuit makes semantic dimensions report <c>Unavailable</c>, an explicit state.
/// It must never be interpreted downstream as "no risk found": an outage is not a clean bill
/// of health, and the local policy decides allow/hold/quarantine from the remaining evidence.
/// </remarks>
internal sealed class JevCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public JevCircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _time = time;
    }

    /// <summary>
    /// True while calls should be skipped. Once the open duration elapses the circuit allows a
    /// single probe through, so recovery is discovered rather than waited out.
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
