namespace StyloMail.Chat.Slack;

/// <summary>
/// Admits an event once, so a platform retry is not assessed as new traffic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded, and the bound is the point.</b> A set that grew with every event would be an
/// unbounded allocation driven by an external caller. Past the capacity the oldest id is evicted and
/// may be admitted again, and that is the right trade: a rare duplicated assessment is recoverable,
/// unbounded growth on an unauthenticated surface is not.
/// </para>
/// <para>
/// <b><see cref="Forget"/> exists for a failure on our side.</b> If the work after admission fails,
/// the caller forgets the id so a genuine retry is admitted rather than being mistaken for a
/// duplicate of an attempt that never completed.
/// </para>
/// <para>
/// Single-threaded per instance. The host calls this from one request at a time per guard, and a
/// shared guard would need its own locking and its own reasoning.
/// </para>
/// </remarks>
public sealed class SlackRetryGuard(int capacity)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool TryBegin(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        if (!_seen.Add(eventId))
        {
            return false;
        }

        _order.Enqueue(eventId);

        while (_order.Count > capacity)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }

    public void Forget(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        _seen.Remove(eventId);
    }
}
