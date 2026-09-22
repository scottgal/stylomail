namespace StyloMail.Host.Traffic;

/// <summary>
/// Announces that something changed, so a console can re-read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole point of this port is that publishing cannot fail.</b> It returns nothing and
/// everything that implements it answers by doing nothing observable to the caller, so no pipeline
/// code can depend on the live feed: a hub outage is invisible to mail flow, and an emission that
/// threw into an assessment or a delivery would be wrong however the events were shaped.
/// </para>
/// <para>
/// <b>It is a port rather than a hub context handed to call sites</b>, and that is the same
/// requirement made structural. A component holding an <c>IHubContext</c> has a dependency on the
/// transport, its failure modes and its lifetime; a component holding this has a call it makes and
/// forgets, and switching the feature off is substituting the no-op rather than remembering not to
/// call anything.
/// </para>
/// <para>
/// <b>Callers are inside the mail path, so a caller must never have to guard a call.</b> Whether
/// the hub exists, whether it is reachable and whether anyone is listening are all decisions below
/// this line, not at the four places that produce a change.
/// </para>
/// </remarks>
public interface ITrafficEvents
{
    /// <summary>
    /// Announces a change. Never throws, and never blocks on the transport.
    /// </summary>
    void Publish(TrafficEvent change);
}

/// <summary>
/// The implementation a deployment gets when it has not enabled live traffic.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the flag-off path, not a placeholder.</b> A deployment that has not configured the
/// feature resolves this and loses immediacy and nothing else, which is the promise the default-off
/// flag makes. Making it a no-op object rather than a null check at each call site means the
/// pipeline reads identically whether the feature is on or off, so enabling it cannot change what
/// any mail path does.
/// </para>
/// <para>
/// The methods are empty on purpose. An implementation that logged would put a line in the log of
/// every deployment that did not ask for the feature, and one that threw would be worse.
/// </para>
/// </remarks>
public sealed class NullTrafficEvents : ITrafficEvents
{
    public static readonly NullTrafficEvents Instance = new();

    private NullTrafficEvents()
    {
    }

    public void Publish(TrafficEvent change)
    {
        // Deliberately empty: see the class remarks.
    }
}
