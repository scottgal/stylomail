using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Api;

/// <summary>
/// Which read of the Host a pushed notice calls for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A route carries nothing the notice carried.</b> There is no member here
/// that could hold a kind, an id or a state, so a caller holding one has nothing
/// it could show an operator and only one thing it can do: read the Host. The
/// console's rule for pushed events, that a notice is a hint and never a
/// rendering, is stated on <see cref="TrafficNotice"/> and enforced here by the
/// absence of anywhere to put the hint's contents.
/// </para>
/// <para>
/// The four reads are the window's own, named for what they cover rather than
/// for the methods that perform them. The naming is the point: the window needs
/// a display, so a mapping that lived inside it could not be asked what an
/// unreadable notice should do, and the one branch that matters most is the one
/// nothing could drive.
/// </para>
/// </remarks>
public enum TrafficNoticeRoute
{
    /// <summary>The status bar: readiness is a fact about the Host and nothing else.</summary>
    Readiness,

    /// <summary>The sidebar, which is where a paused sender shows.</summary>
    Senders,

    /// <summary>Whichever listing is open, because a row moved in it.</summary>
    Selection,

    /// <summary>
    /// The whole visible surface, for a notice this build cannot pin down.
    /// </summary>
    /// <remarks>
    /// Always correct and merely less precise: the read is the truth and the
    /// notice was only a nudge toward it. The alternative, dropping a hint this
    /// build cannot read, is the silently frozen feed the live surface exists to
    /// prevent, so the answer to "I do not know what changed" is "then read
    /// everything" rather than "then do nothing".
    /// </remarks>
    Everything,
}

/// <summary>
/// Turns a notice into the read it calls for.
/// </summary>
/// <remarks>
/// A pure function over the notice and nothing else, so the routing has a test
/// of its own: the window that consumes it is an Avalonia control and is not
/// reachable from a unit test, and the unrecognised-kind branch in particular
/// could not be exercised anywhere else.
/// </remarks>
public static class TrafficNoticeRouting
{
    /// <summary>The read to perform for this notice.</summary>
    /// <remarks>
    /// The argument is the notice rather than its <see cref="TrafficNotice.Recognised"/>
    /// so that the raw string, the recognition and the route are one path a test
    /// can walk end to end. A caller that recognised the kind itself and passed
    /// the enum would leave the string in the window, which is where a kind from
    /// a newer Host arrives from and where nothing can assert what happens to it.
    /// </remarks>
    public static TrafficNoticeRoute For(TrafficNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return notice.Recognised switch
        {
            // A fact about the Host and about nothing else, so the status bar
            // is the whole affected surface.
            TrafficNoticeKind.ReadinessChanged => TrafficNoticeRoute.Readiness,

            // Where a paused sender shows is the sidebar, and a control change
            // alters no listing. Re-reading the middle pane here would make it
            // flicker for no new information.
            TrafficNoticeKind.SenderControlChanged => TrafficNoticeRoute.Senders,

            // A row moved in whichever listing is open. Which listing that is
            // belongs to the selection, so the selection is reloaded.
            TrafficNoticeKind.DecisionRecorded or TrafficNoticeKind.MessageStateChanged
                => TrafficNoticeRoute.Selection,

            // A kind this build does not know, or none at all. Not an error and
            // not dropped: a full re-read is always correct, and the notice's
            // own contents cannot narrow it because the operator's guess at what
            // a newer Host meant is not knowledge.
            _ => TrafficNoticeRoute.Everything,
        };
    }
}
