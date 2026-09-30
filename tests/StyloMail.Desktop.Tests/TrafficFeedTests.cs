using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The live feed, on the parts of it that do not need a Host.
/// </summary>
/// <remarks>
/// What is testable here without a network is everything that decides whether
/// the console tries at all, and everything that phrases what happened. Those
/// are the parts with rules in them. Whether a socket actually opens is a fact
/// about the Host and lives in <see cref="LiveTrafficTests"/>.
/// </remarks>
public sealed class TrafficFeedTests
{
    private static readonly Uri HarnessHost = new("http://127.0.0.1:5271");

    // ===================== where the feed lives =====================

    [Fact]
    public void The_feed_url_is_the_host_address_with_the_hubs_path()
    {
        var address = TrafficFeed.HubAddress(HarnessHost);

        Assert.Equal("http://127.0.0.1:5271/v1/traffic", address.ToString().TrimEnd('/'));
    }

    /// <summary>
    /// A Host address carrying a query must not carry it into the feed's URL.
    /// </summary>
    /// <remarks>
    /// This is the one place where a query string could get into a URL this
    /// console sends, and the Hub takes nothing that way on purpose. The
    /// console is documented as never putting its key in a URL, so a URL built
    /// by concatenation is the hazard, and this asserts the builder does not
    /// reintroduce it from wherever the address came from.
    /// </remarks>
    [Fact]
    public void The_feed_url_drops_a_query_and_a_fragment_from_the_host_address()
    {
        var address = TrafficFeed.HubAddress(new Uri("http://127.0.0.1:5271/?access_token=oops#frag"));

        Assert.Equal("http://127.0.0.1:5271/v1/traffic", address.ToString().TrimEnd('/'));
        Assert.DoesNotContain("access_token", address.ToString(), StringComparison.Ordinal);
    }

    // ===================== when the console refuses to try =====================

    [Fact]
    public async Task A_console_with_no_key_does_not_open_a_feed()
    {
        await using var feed = new TrafficFeed(HarnessHost, new TestApiKeyProvider(key: null));

        var state = await feed.StartAsync(CancellationToken.None);

        Assert.Equal(TrafficFeedState.Refused, state);
    }

    /// <summary>
    /// The transport rule applies to the socket as well as to the requests.
    /// </summary>
    /// <remarks>
    /// The feed carries the same header as every other call, so an unencrypted
    /// socket to another machine puts the operator's key on the wire in clear
    /// text exactly as a plain HTTP request would. The address policy is the
    /// same function the connection screen uses, so the two cannot disagree.
    /// </remarks>
    [Fact]
    public async Task A_plain_http_feed_to_another_machine_is_refused()
    {
        await using var feed = new TrafficFeed(
            new Uri("http://stylomail.example.test:8080"),
            new TestApiKeyProvider());

        var state = await feed.StartAsync(CancellationToken.None);

        Assert.Equal(TrafficFeedState.Refused, state);
    }

    // ===================== what a notice means =====================

    [Theory]
    [InlineData("DecisionRecorded", TrafficNoticeKind.DecisionRecorded)]
    [InlineData("MessageStateChanged", TrafficNoticeKind.MessageStateChanged)]
    [InlineData("SenderControlChanged", TrafficNoticeKind.SenderControlChanged)]
    [InlineData("ReadinessChanged", TrafficNoticeKind.ReadinessChanged)]
    public void A_kind_the_host_sends_is_recognised(string wire, TrafficNoticeKind expected)
        => Assert.Equal(expected, new TrafficNotice { Kind = wire }.Recognised);

    /// <summary>
    /// A kind this build does not know is not an error and is not the zero member.
    /// </summary>
    /// <remarks>
    /// The console binds this one field as a string where every other contract
    /// binds a closed enum, and this is the behaviour that buys. Binding an
    /// enum would throw here, end the feed, and leave an operator with a
    /// dropped connection and no reason for it, over a hint from a newer Host
    /// that this build could have answered by reading the screen again.
    /// </remarks>
    [Fact]
    public void An_unrecognised_kind_is_kept_and_is_not_taken_for_a_known_one()
    {
        var notice = new TrafficNotice { Kind = "SomethingNewer" };

        Assert.Null(notice.Recognised);

        // Distinct from the zero member, which is DecisionRecorded and would
        // have the console re-read the wrong surface while believing it knew
        // what the Host said.
        Assert.NotEqual((TrafficNoticeKind?)TrafficNoticeKind.DecisionRecorded, notice.Recognised);
    }

    [Fact]
    public void A_notice_with_no_kind_at_all_is_not_taken_for_a_known_one()
        => Assert.Null(new TrafficNotice { Kind = null }.Recognised);

    // ===================== what the operator is told =====================

    /// <summary>
    /// A deployment with no feed is not a deployment whose screen is stale.
    /// </summary>
    /// <remarks>
    /// The single most important claim this type makes. Nothing in such a
    /// console was ever following the Host, so every screen was read when it
    /// was opened and none of it is behind. A console that cried stale here
    /// would be crying wolf on the state most deployments are in.
    /// </remarks>
    [Fact]
    public void A_deployment_with_no_feed_is_not_reported_as_stale()
    {
        var status = LiveFeedStatus.From(TrafficFeedState.NoFeed, screenMayBeStale: true);

        Assert.False(status.ScreenMayBeStale);
    }

    [Fact]
    public void A_feed_that_stopped_is_reported_as_a_screen_that_may_be_stale()
    {
        var status = LiveFeedStatus.From(TrafficFeedState.Dropped, screenMayBeStale: true);

        Assert.True(status.ScreenMayBeStale);
        Assert.Contains("may be out of date", status.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the screen has been read again, it is no longer out of date, even
    /// though the feed has not come back.
    /// </summary>
    [Fact]
    public void A_screen_read_since_the_feed_stopped_is_no_longer_stale()
    {
        var status = LiveFeedStatus.From(TrafficFeedState.Dropped, screenMayBeStale: false);

        Assert.False(status.ScreenMayBeStale);
        Assert.DoesNotContain("may be out of date", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void No_state_that_was_never_live_is_reported_as_stale()
    {
        foreach (var state in new[]
                 {
                     TrafficFeedState.NotStarted,
                     TrafficFeedState.Connecting,
                     TrafficFeedState.NoFeed,
                     TrafficFeedState.Refused,
                     TrafficFeedState.Unreachable,
                 })
        {
            Assert.False(
                LiveFeedStatus.From(state, screenMayBeStale: true).ScreenMayBeStale,
                $"{state} was never live and cannot have fallen behind");
        }
    }

    [Fact]
    public void Every_state_is_phrased_with_a_headline_and_a_detail()
    {
        foreach (var state in Enum.GetValues<TrafficFeedState>())
        {
            var status = LiveFeedStatus.From(state, screenMayBeStale: false);

            Assert.False(string.IsNullOrWhiteSpace(status.Headline), $"{state} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(status.Detail), $"{state} has no detail");
        }
    }

    /// <summary>
    /// The state a console starts in is phrased, not blank.
    /// </summary>
    [Fact]
    public void A_feed_that_was_never_started_is_phrased()
    {
        var status = LiveFeedStatus.From(TrafficFeedState.NotStarted, screenMayBeStale: false);

        Assert.Equal(TrafficFeedState.NotStarted, status.Kind);
        Assert.False(string.IsNullOrWhiteSpace(status.Headline));
    }
}
