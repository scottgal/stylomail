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
/// <b>The rule this type exists to hold is that "not live" and "possibly out of date" are
/// different claims, and that the second belongs to this console's history
/// rather than to the state the last attempt happened to produce.</b> A console
/// that has never followed anything read every screen when it was opened, so
/// nothing on it is stale; a console that was following the Host and then
/// stopped is the opposite situation, and what is on its screen is the last
/// thing the feed said. The state cannot tell those two apart. An operator's own
/// Reconnect after an outage lands in exactly the state a deployment with no
/// feed produces, which is the reachable path that made the state exemption this
/// type used to carry wrong. What separates them is whether this console was
/// ever live and has not re-read since, and that is what
/// <see cref="ScreenMayBeStale"/> records.
/// </para>
/// </remarks>
public sealed record LiveFeedStatus
{
    /// <summary>
    /// What every stale headline says, appended to the state's own words.
    /// </summary>
    /// <remarks>
    /// The sentence is appended rather than substituted so the headline keeps
    /// saying which state the console is in. For <see cref="TrafficFeedState.Dropped"/>
    /// "Live updates stopped" plus this is the wording this console has always
    /// shown, which is why the suffix is these exact words.
    /// </remarks>
    private const string StaleSuffix = ", screen may be out of date";

    private LiveFeedStatus(
        TrafficFeedState kind,
        string headline,
        string detail,
        bool screenMayBeStale,
        FeedRetry? retry)
    {
        Kind = kind;
        Headline = headline;
        Detail = detail;
        ScreenMayBeStale = screenMayBeStale;
        Retry = retry;
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
    /// True for any state a live console has fallen into, and cleared when the
    /// visible surface is read again. See <see cref="From"/> for why the state
    /// does not decide it.
    /// </remarks>
    public bool ScreenMayBeStale { get; }

    /// <summary>
    /// The retry in flight, or null when the console is not retrying.
    /// </summary>
    /// <remarks>
    /// Carried through from the feed rather than counted here: the sequence is
    /// the only thing that knows which attempt it is on, and this type's job is
    /// to say it in words.
    /// </remarks>
    public FeedRetry? Retry { get; }

    /// <summary>
    /// What the console is doing about a feed that will not open, in one line,
    /// or empty when it has stopped trying.
    /// </summary>
    /// <remarks>
    /// <b>Empty rather than absent is the point.</b> The status bar renders this
    /// beside the feed's own headline, and a control that came and went could
    /// not be asserted absent by the harness: a locator matching no control at
    /// all fails rather than reporting invisible.
    /// </remarks>
    public string RetryHeadline
    {
        get
        {
            if (Retry is not { } retry)
            {
                return string.Empty;
            }

            var counter = $"Retrying: attempt {retry.Retry} of {retry.Retries}";

            return retry.Waiting is { } wait && wait > TimeSpan.Zero
                ? $"{counter}, next in {(int)wait.TotalSeconds} seconds"
                : counter;
        }
    }

    /// <summary>Phrases a feed state, whether the surface has been read since, and any retry running.</summary>
    /// <param name="state">Where the feed stands.</param>
    /// <param name="screenMayBeStale">
    /// Whether the console has been live and has not read the surface since.
    /// Passed in rather than derived, because a console that has since re-read
    /// what is on screen is no longer showing anything out of date even though
    /// its feed is still down.
    /// </param>
    /// <param name="retry">The retry in flight, if an operator's connect is running.</param>
    /// <remarks>
    /// <b>The flag, not the state, decides whether the stale sentence is
    /// rendered, and no state is exempt from that.</b> Which failure the feed
    /// settled in is a fact about the last attempt; whether the screen can be
    /// trusted is a fact about this console's history, and after a live feed the
    /// second is true whatever the first says. Gating it on
    /// <see cref="TrafficFeedState.Dropped"/> was wrong in the one case that
    /// matters: an operator's own Reconnect during an outage settles in
    /// <see cref="TrafficFeedState.Unreachable"/>, and the warning used to
    /// disappear at the moment the console most needed to keep saying it.
    /// Exempting <see cref="TrafficFeedState.NoFeed"/> was the same mistake one
    /// state over, and the same operator action reaches it: a Reconnect against
    /// a Host that has since stopped mapping the hub route answers 404, so the
    /// sequence ends in <c>NoFeed</c> with the flag still set and nothing
    /// re-read. Every pane keeps showing the read from before the change, and
    /// the console has to keep saying so.
    /// </remarks>
    public static LiveFeedStatus From(
        TrafficFeedState state,
        bool screenMayBeStale,
        FeedRetry? retry = null)
    {
        // The flag alone, with no state exempt from it. A console that never
        // followed anything has it false by construction, since it is set only
        // in the handlers of a connection that started; and a console that has
        // re-read the visible surface since going live has it false too, because
        // SurfaceIsCurrent is the only thing that clears it. Those are exactly
        // the two cases where "not stale" is the truthful answer, so a state
        // clause here can only ever remove a warning that is owed: NoFeed is
        // reachable from Live, through a Reconnect that answers 404, with the
        // flag still true and every open pane still holding the old read.
        var stale = screenMayBeStale;

        var (headline, detail) = state switch
        {
            TrafficFeedState.Live => (
                "Live",
                "The Host is pushing changes as they happen, and every screen here is read back from it."),

            TrafficFeedState.Connecting => (
                "Connecting",
                "Asking this Host for its live feed."),

            // A finished answer rather than a fault: the Host maps this route
            // only when the deployment asked for a feed, so its absence is a
            // deployment that does not have one, which is a complete
            // configuration.
            //
            // What this sentence must not do is rule on staleness. "There was
            // never a feed to fall behind" is a claim about the console, and it
            // is false for one that was live until a Reconnect answered 404: the
            // headline beside this tooltip says that console may be out of date
            // and is right. So this says what the deployment is and how screens
            // are read, and leaves the console's own history to the headline.
            TrafficFeedState.NoFeed => (
                "No live feed",
                "This deployment does not offer one, which is a configuration rather than a fault. "
                + "Screens are read from the Host when you open them."),

            TrafficFeedState.Refused => (
                "Live feed not authorised",
                "The Host would not open a feed for this console's key. Reviewing is the privilege the feed "
                + "needs, and it is granted separately from sending."),

            TrafficFeedState.Unreachable => (
                "No live feed",
                "Nothing answered at the feed's address. Screens are still read from the Host when you open "
                + "them, so this console works without it."),

            // The one state that costs something, and the reason this type
            // exists. The operator was watching something that was following
            // the Host, and is now looking at the last thing it said.
            TrafficFeedState.Dropped => (
                "Live updates stopped",
                "The feed was connected and is not. What is on screen is what it last said, not what the "
                + "Host says now; open a destination to read it again."),

            _ => (
                "Live updates not started",
                "This console has not asked the Host for a live feed yet."),
        };

        return new LiveFeedStatus(state, stale ? headline + StaleSuffix : headline, detail, stale, retry);
    }
}
