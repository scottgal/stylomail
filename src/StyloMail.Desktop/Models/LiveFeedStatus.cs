using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Models;

/// <summary>
/// What the console is doing about live updates, phrased for the person reading it.
/// </summary>
/// <remarks>
/// <para>
/// A plain value rather than a view model, on the same reasoning as
/// <see cref="HostStatus"/>: the mapping from a connection state to a sentence
/// an operator can act on is the console's diagnostic surface, and it is worth
/// more than the pixels it is rendered with. This can be tested exhaustively
/// with no display and no dispatcher.
/// </para>
/// <para>
/// <b>The load-bearing rule is that "not live" and "possibly out of date" are
/// different claims.</b> A deployment with no feed has never followed anything,
/// and every screen in it was read when it was opened, so nothing there is
/// stale and saying otherwise would train an operator to ignore the warning. A
/// feed that <em>stopped</em> is the opposite situation: the operator was
/// watching something that was following the Host a moment ago, and the screen
/// is now the last thing it said. That is what <see cref="ScreenMayBeStale"/>
/// exists to distinguish, and it is the reason this type is not just a mapping
/// from <see cref="TrafficFeedState"/>.
/// </para>
/// </remarks>
public sealed record LiveFeedStatus
{
    private LiveFeedStatus(
        TrafficFeedState kind,
        string headline,
        string detail,
        bool screenMayBeStale)
    {
        Kind = kind;
        Headline = headline;
        Detail = detail;
        ScreenMayBeStale = screenMayBeStale;
    }

    /// <summary>The connection state this was phrased from.</summary>
    public TrafficFeedState Kind { get; }

    /// <summary>Two or three words: the state, said plainly.</summary>
    public string Headline { get; }

    /// <summary>What it means for what is on screen, and what to do about it.</summary>
    public string Detail { get; }

    /// <summary>
    /// Whether what is on screen may have moved on since it was read.
    /// </summary>
    /// <remarks>
    /// True only for a feed that was connected and stopped, and cleared when
    /// the visible surface is read again. See the remarks on this type.
    /// </remarks>
    public bool ScreenMayBeStale { get; }

    /// <summary>Phrases a feed state and whether the visible surface has been read since.</summary>
    /// <param name="state">Where the feed stands.</param>
    /// <param name="screenMayBeStale">
    /// Whether the feed has reported a gap this console has not yet read past.
    /// Passed in rather than derived, because a console that has since re-read
    /// what is on screen is no longer showing anything out of date even though
    /// its feed is still down.
    /// </param>
    public static LiveFeedStatus From(TrafficFeedState state, bool screenMayBeStale)
    {
        var stale = screenMayBeStale && state is TrafficFeedState.Dropped;

        return state switch
        {
            TrafficFeedState.Live => new LiveFeedStatus(
                state,
                "Live",
                "The Host is pushing changes as they happen, and every screen here is read back from it.",
                stale),

            TrafficFeedState.Connecting => new LiveFeedStatus(
                state,
                "Connecting",
                "Asking this Host for its live feed.",
                stale),

            // A finished answer, not a fault, and not stale. The Host maps this
            // route only when the deployment asked for a feed, so its absence
            // is a deployment that does not have one, which is a complete
            // configuration and not a broken console.
            TrafficFeedState.NoFeed => new LiveFeedStatus(
                state,
                "No live feed",
                "This deployment does not offer one. Screens are read from the Host when you open them, "
                + "so nothing here is out of date: there was never a feed to fall behind.",
                false),

            TrafficFeedState.Refused => new LiveFeedStatus(
                state,
                "Live feed not authorised",
                "The Host would not open a feed for this console's key. Reviewing is the privilege the feed "
                + "needs, and it is granted separately from sending.",
                stale),

            TrafficFeedState.Unreachable => new LiveFeedStatus(
                state,
                "No live feed",
                "Nothing answered at the feed's address. Screens are still read from the Host when you open "
                + "them, so this console works without it.",
                false),

            // The one state that costs something, and the reason this type
            // exists. The operator was watching something that was following
            // the Host, and is now looking at the last thing it said.
            TrafficFeedState.Dropped => new LiveFeedStatus(
                state,
                stale ? "Live updates stopped, screen may be out of date" : "Live updates stopped",
                "The feed was connected and is not. What is on screen is what it last said, not what the "
                + "Host says now; open a destination to read it again.",
                stale),

            _ => new LiveFeedStatus(
                state,
                "Live updates not started",
                "This console has not asked the Host for a live feed yet.",
                false),
        };
    }
}
