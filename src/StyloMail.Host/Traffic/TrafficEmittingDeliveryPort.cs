using StyloMail.Queue;

namespace StyloMail.Host.Traffic;

/// <summary>
/// The delivery port the worker dials through, announcing that a message's state moved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator rather than a callback into the worker.</b> The state transaction belongs to
/// the queue and the hosting belongs to this host, so the seam between them is the port: wrapping it
/// is the one place this lane can see a delivery settle without either lane learning about the
/// other. An observer added to the worker would have been a change to a component this lane does
/// not own, made for a reason that component has no stake in.
/// </para>
/// <para>
/// <b>A settled delivery and a port fault are both state changes.</b> The worker records the second
/// as a per-recipient temporary failure, which moves the message's state exactly as the first does,
/// so both are announced. A cancellation is not: the worker rethrows, records nothing, and leaves
/// the lease to expire, which means the row did not move and a notice saying it did would be a
/// false hint.
/// </para>
/// <para>
/// <b>The inner port's behaviour is passed through untouched.</b> Its result is returned as it came,
/// and its exception is rethrown as it came, because this wrapper exists to announce a change and
/// not to change one.
/// </para>
/// </remarks>
public sealed class TrafficEmittingDeliveryPort : IDeliveryPort
{
    private readonly IDeliveryPort _inner;
    private readonly ITrafficEvents _events;
    private readonly TimeProvider _clock;

    public TrafficEmittingDeliveryPort(IDeliveryPort inner, ITrafficEvents events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _inner = inner;
        _events = events;
        _clock = clock;
    }

    /// <summary>The port this one dials, so a composition decision is assertable rather than implied.</summary>
    public IDeliveryPort Inner => _inner;

    public async ValueTask<DeliveryPortResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DeliveryPortResult delivered;

        try
        {
            delivered = await _inner.DeliverAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our own drain window closed, or the host is shutting down. Nothing was recorded and
            // the lease is left to expire, so the message's state has not moved.
            throw;
        }
        catch (Exception)
        {
            // The worker records a temporary failure against every pending recipient, which is a
            // change to this message, so it is announced on the way past.
            Announce(request);
            throw;
        }

        // Outside the try, deliberately. An announcement inside it would be caught by the handler
        // above and read as the inner port having faulted, so the message would be announced twice
        // and reported as a transport failure it never had. The announcement cannot throw anyway,
        // but the shape should not depend on that being true.
        Announce(request);
        return delivered;
    }

    private void Announce(DeliveryRequest request)
        => _events.Publish(TrafficEvent.MessageStateChanged(
            request.TenantId,
            request.QueueId,
            _clock.GetUtcNow()));
}
