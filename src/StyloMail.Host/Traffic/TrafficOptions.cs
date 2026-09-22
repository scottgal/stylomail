namespace StyloMail.Host.Traffic;

/// <summary>
/// Bound from the <c>StyloMail:Traffic</c> configuration section: whether this deployment offers
/// the console a live feed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and off is a complete deployment.</b> A host that has not enabled this loses
/// immediacy and nothing else: the feed is a hint that a row moved, the console's existing polling
/// is what reads the rows, and every emission happens through a port whose default implementation
/// does nothing. Enabling it must not be a prerequisite for the console working.
/// </para>
/// <para>
/// There is deliberately nothing else in here. A hub path, a page size or a keepalive interval
/// would each be a knob whose only effect is on a client that already knows its own transport.
/// </para>
/// </remarks>
public sealed class TrafficOptions
{
    public const string SectionName = "StyloMail:Traffic";

    /// <summary>Whether the host offers the hub at all. Off by default.</summary>
    /// <remarks>
    /// Read when the port is resolved rather than at registration, so the decision is made from
    /// configuration that is final. The host's composition root runs before a test host layers its
    /// own configuration in, and a registration-time read would answer from the wrong values.
    /// </remarks>
    public bool Enabled { get; set; }
}
