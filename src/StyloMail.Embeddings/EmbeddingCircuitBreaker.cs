namespace StyloMail.Embeddings;

/// <summary>
/// Trips after repeated failures so an embedding service that is down or thrashing cannot consume a
/// caller's latency budget on every segment.
/// </summary>
/// <remarks>
/// <para>
/// An open circuit makes every row <c>NotProduced</c> with <see cref="EmbeddingNotProducedReason.CircuitOpen"/>.
/// That is an explicit absence, and it must not be read downstream as "nothing similar found".
/// </para>
/// <para>
/// <b>A small deliberate copy rather than a shared type.</b> The same shape exists in
/// <c>StyloMail.Nimble</c> and in the hosted adapter, each internal to its assembly. Moving one into
/// Core would be a change to a file this lane does not own, and the type depends on nothing but a
/// clock, so the duplication is bounded and tested here.
/// </para>
/// </remarks>
internal sealed class EmbeddingCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public EmbeddingCircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeProvider time)
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
