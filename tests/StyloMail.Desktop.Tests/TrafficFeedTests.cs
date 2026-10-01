using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
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

    /// <summary>
    /// Once a console has been live, every state it falls into still carries the warning.
    /// </summary>
    /// <remarks>
    /// <b>The flag decides, not the state.</b> Which failure the last attempt
    /// settled in is a fact about the attempt; whether the screen can be trusted
    /// is a fact about this console's history. Gating the warning on
    /// <see cref="TrafficFeedState.Dropped"/> looked right and was wrong in the
    /// one case that matters: an operator's own Reconnect during an outage
    /// settles in <see cref="TrafficFeedState.Unreachable"/>, and the sentence
    /// vanished at the moment the console most needed to keep saying it.
    /// </remarks>
    [Fact]
    public void Every_state_other_than_no_feed_carries_the_stale_warning()
    {
        foreach (var state in Enum.GetValues<TrafficFeedState>())
        {
            if (state is TrafficFeedState.NoFeed)
            {
                // The one exemption, and it is a rule: see
                // A_deployment_with_no_feed_is_not_reported_as_stale.
                continue;
            }

            var status = LiveFeedStatus.From(state, screenMayBeStale: true);

            Assert.True(status.ScreenMayBeStale, $"{state} after a live feed is a screen that may be behind");
            Assert.Contains("may be out of date", status.Headline, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A subscription that replaces a live one can be told the screen is behind.
    /// </summary>
    /// <remarks>
    /// The window replaces the feed on every reconnect, so without this the
    /// warning an outage raised would be dropped by the very act of trying to
    /// recover, and the console would claim a currency it does not have.
    /// </remarks>
    [Fact]
    public void A_replacement_subscription_can_be_told_the_screen_is_already_behind()
    {
        var feed = new TrafficFeed(HarnessHost, new TestApiKeyProvider(), surfaceMayBeStale: true);

        Assert.True(feed.SurfaceMayBeStale);

        var fresh = new TrafficFeed(HarnessHost, new TestApiKeyProvider());

        Assert.False(fresh.SurfaceMayBeStale);
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

    // ===================== the cadence =====================

    /// <summary>
    /// One table, and it is the one an operator is told about.
    /// </summary>
    /// <remarks>
    /// The automatic path and the operator's path are handed this same object,
    /// so "how long until the console gives up" has one answer rather than two
    /// that can drift. The numbers are SignalR's own defaults, written down here
    /// so the budget is something this code states rather than inherits.
    /// </remarks>
    [Fact]
    public void The_shared_cadence_waits_nothing_then_two_then_ten_then_thirty_seconds()
    {
        Assert.Equal(4, TrafficRetryPolicy.Shared.RetryLimit);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)],
            Enumerable.Range(1, 4).Select(retry => TrafficRetryPolicy.Shared.DelayBeforeRetry(retry)!.Value));
        Assert.Equal(TimeSpan.FromSeconds(42), TrafficRetryPolicy.Shared.Budget);
    }

    [Fact]
    public void The_cadence_stops_after_its_last_wait()
    {
        Assert.Null(TrafficRetryPolicy.Shared.DelayBeforeRetry(5));
        Assert.Null(TrafficRetryPolicy.Shared.DelayBeforeRetry(0));
    }

    /// <summary>
    /// SignalR's own interface gets the same cadence as the console's loop.
    /// </summary>
    /// <remarks>
    /// This is the seam that keeps the two paths one policy rather than two
    /// copies of one table. <c>PreviousRetryCount</c> counts failures already
    /// suffered, so the wait after the first is the table's first entry.
    /// </remarks>
    [Fact]
    public void SignalRs_automatic_reconnect_gets_the_same_cadence()
    {
        var policy = TrafficRetryPolicy.Shared;

        Assert.Equal(TimeSpan.Zero, policy.NextRetryDelay(Context(previousFailures: 0)));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextRetryDelay(Context(previousFailures: 1)));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextRetryDelay(Context(previousFailures: 3)));

        // Null is how the library is told to stop, which is what makes the
        // console's own "stopped" state reachable at all.
        Assert.Null(policy.NextRetryDelay(Context(previousFailures: 4)));
    }

    /// <summary>
    /// A failed attempt, as SignalR describes one to a retry policy.
    /// </summary>
    /// <remarks>
    /// The other two fields are set because the library's shape requires them,
    /// and neither is read here: only how many attempts have already failed
    /// decides which wait comes next.
    /// </remarks>
    private static RetryContext Context(long previousFailures) => new()
    {
        PreviousRetryCount = previousFailures,
        ElapsedTime = TimeSpan.Zero,
        RetryReason = new InvalidOperationException("the probe's failure"),
    };

    /// <summary>A policy with no delays would never retry, so it is refused.</summary>
    [Fact]
    public void A_cadence_with_no_waits_is_refused()
        => Assert.Throws<ArgumentException>(() => new TrafficRetryPolicy());

    // ===================== what the operator is told about it =====================

    [Fact]
    public void A_retry_says_which_attempt_it_is_and_how_long_is_left()
    {
        var status = LiveFeedStatus.From(
            TrafficFeedState.Unreachable,
            screenMayBeStale: true,
            new FeedRetry(2, 4, TimeSpan.FromSeconds(10)));

        Assert.Equal("Retrying: attempt 2 of 4, next in 10 seconds", status.RetryHeadline);
    }

    [Fact]
    public void A_retry_that_is_under_way_says_only_which_attempt_it_is()
    {
        var status = LiveFeedStatus.From(
            TrafficFeedState.Connecting,
            screenMayBeStale: true,
            new FeedRetry(2, 4, Waiting: null));

        Assert.Equal("Retrying: attempt 2 of 4", status.RetryHeadline);
    }

    /// <summary>
    /// Not retrying is said in words the harness can read.
    /// </summary>
    /// <remarks>
    /// Empty rather than a hidden control: a locator matching no control at all
    /// fails in the harness rather than reporting invisible, so a line that came
    /// and went could not be asserted gone.
    /// </remarks>
    [Fact]
    public void Nothing_is_said_when_nothing_is_being_retried()
        => Assert.Equal(
            string.Empty,
            LiveFeedStatus.From(TrafficFeedState.Live, screenMayBeStale: false).RetryHeadline);

    // ===================== the sequence =====================

    /// <summary>
    /// A Host that is not there gets every attempt the cadence allows, and then no more.
    /// </summary>
    /// <remarks>
    /// Port 1 on the loopback interface has nothing behind it, so every attempt
    /// is refused instantly and the whole sequence runs on the fast policy in
    /// milliseconds. What is asserted is the shape of the sequence: the waits
    /// announced are the policy's, once each and in order, and it ends by
    /// stopping rather than by throwing.
    /// </remarks>
    [Fact]
    public async Task An_operator_sequence_makes_every_attempt_and_then_stops()
    {
        var attempts = 0;
        var announced = new ConcurrentQueue<FeedRetry?>();

        // The last retry this test saw, so that the several state changes that
        // happen while one wait is standing are not counted as several waits.
        FeedRetry? last = null;

        await using var feed = new TrafficFeed(DeadHost, new TestApiKeyProvider(), FastPolicy);

        feed.StateChanged += () =>
        {
            if (feed.State is TrafficFeedState.Connecting)
            {
                Interlocked.Increment(ref attempts);
            }

            if (feed.Retrying != last)
            {
                last = feed.Retrying;
                announced.Enqueue(last);
            }
        };

        var state = await feed.StartWithRetryAsync(CancellationToken.None);

        Assert.Equal(TrafficFeedState.Unreachable, state);
        Assert.Equal(FastPolicy.RetryLimit + 1, Volatile.Read(ref attempts));

        // Announced once before each wait, and cleared once the attempt itself
        // is under way: the difference between "in 30 seconds" and "now".
        var waits = announced.Where(retry => retry is { Waiting: { } }).ToList();
        var underWay = announced.Count(retry => retry is { Waiting: null });

        Assert.Equal(FastPolicy.RetryLimit, waits.Count);
        Assert.Equal(FastPolicy.RetryLimit, underWay);
        Assert.Equal(
            Enumerable.Range(1, FastPolicy.RetryLimit).Select(retry => FastPolicy.DelayBeforeRetry(retry)!.Value),
            waits.Select(retry => retry!.Value.Waiting!.Value));
        Assert.Equal(
            Enumerable.Range(1, FastPolicy.RetryLimit),
            waits.Select(retry => retry!.Value.Retry));

        // Giving up is the same as not trying any more, which is what the
        // status bar renders as nothing at all.
        Assert.Null(feed.Retrying);
    }

    /// <summary>
    /// An answer the deployment has already given is not retried.
    /// </summary>
    /// <remarks>
    /// A refusal will not become an acceptance inside forty-two seconds, and
    /// re-presenting a key the Host has just rejected is a worse failure than
    /// the one being retried. The state is refused before any socket opens, so
    /// this costs no network at all.
    /// </remarks>
    [Fact]
    public async Task An_operator_sequence_does_not_retry_a_refusal()
    {
        var attempts = 0;

        await using var feed = new TrafficFeed(
            new Uri("http://stylomail.example.test:8080"),
            new TestApiKeyProvider(),
            FastPolicy);

        feed.StateChanged += () =>
        {
            if (feed.State is TrafficFeedState.Connecting)
            {
                Interlocked.Increment(ref attempts);
            }
        };

        var state = await feed.StartWithRetryAsync(CancellationToken.None);

        Assert.Equal(TrafficFeedState.Refused, state);
        Assert.Equal(0, Volatile.Read(ref attempts));
        Assert.Null(feed.Retrying);
    }

    /// <summary>
    /// A second call while a sequence is running does not start another.
    /// </summary>
    /// <remarks>
    /// The second of the three conditions a bounded retry has to meet. Without
    /// it a press during the thirty-second wait would rebuild the feed and start
    /// the count again, so impatience would keep the console knocking forever,
    /// which is the opposite of bounded.
    /// </remarks>
    [Fact]
    public async Task A_second_call_while_a_sequence_is_running_does_not_start_another()
    {
        var secondWait = TimeSpan.FromMilliseconds(500);
        var policy = new TrafficRetryPolicy(TimeSpan.Zero, secondWait);
        var attempts = 0;

        await using var feed = new TrafficFeed(DeadHost, new TestApiKeyProvider(), policy);

        feed.StateChanged += () =>
        {
            if (feed.State is TrafficFeedState.Connecting)
            {
                Interlocked.Increment(ref attempts);
            }
        };

        var running = feed.StartWithRetryAsync(CancellationToken.None);

        Assert.True(
            await WaitUntilAsync(() => feed.Retrying is not null),
            "the sequence never announced a retry, so there was nothing to interrupt");

        var whileRunning = Volatile.Read(ref attempts);

        var second = await feed.StartWithRetryAsync(CancellationToken.None);

        // Returned where the running sequence stands, having started nothing.
        Assert.Equal(TrafficFeedState.Unreachable, second);
        Assert.Equal(whileRunning, Volatile.Read(ref attempts));

        await running;

        // And the first sequence still made exactly the attempts it was allowed,
        // so the second call neither restarted it nor doubled it.
        Assert.Equal(policy.RetryLimit + 1, Volatile.Read(ref attempts));
    }

    /// <summary>A Host with nothing behind it. Loopback, so the transport rule allows it.</summary>
    private static Uri DeadHost { get; } = new("http://127.0.0.1:1");

    /// <summary>The shared cadence, in milliseconds, so a whole sequence is instant.</summary>
    private static TrafficRetryPolicy FastPolicy { get; } = new(
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1));

    /// <summary>Waits for a condition the sequence raises from another thread.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}
