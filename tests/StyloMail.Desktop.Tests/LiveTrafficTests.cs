using System.Collections.Concurrent;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The live feed against a Host that is actually running.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that decides whether the classification is honest.</b>
/// The Host maps its Hub only when the deployment enabled it, so a console can
/// meet exactly two good outcomes and no more: a feed that opens, or a 404
/// saying this deployment has none. Every other answer is a fault this test
/// fails on. That matters because the tempting reading of a 404 is
/// "unreachable", which would send an operator to check a Host that is
/// answering them perfectly well, and a test that accepted either sentence
/// would not notice.
/// </para>
/// <para>
/// It runs in both harness configurations: with <c>StyloMail__Traffic__Enabled</c>
/// set it exercises the feed, and without it, the absence. Both are real
/// states of a real deployment and both are asserted here rather than one
/// being skipped.
/// </para>
/// </remarks>
public sealed class LiveTrafficTests
{
    /// <summary>The principal the harness Host configures. See console-harness.sh.</summary>
    private const string HarnessPrincipal = "harness";

    private static Uri HostAddress()
        => new(Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!);

    private static StyloMailApiClient Client()
        => new(
            new HttpClient { BaseAddress = HostAddress() },
            new EnvironmentApiKeyProvider());

    /// <summary>
    /// The feed opens, or the deployment says it has none, and nothing else.
    /// </summary>
    [LiveHostFact]
    public async Task A_running_host_either_offers_a_feed_or_says_it_has_none()
    {
        await using var feed = new TrafficFeed(HostAddress(), new EnvironmentApiKeyProvider());

        var state = await feed.StartAsync(TestTimeout());

        Assert.True(
            state is TrafficFeedState.Live or TrafficFeedState.NoFeed,
            $"A console meeting a Host has two honest answers, a feed or a 404. Got {state}.");
    }

    /// <summary>
    /// When the feed is offered, a real change on the Host arrives on it.
    /// </summary>
    /// <remarks>
    /// The change is driven through the console's own client, so the notice is
    /// the Host telling this console about the write this console just made.
    /// Nothing is stubbed and nothing is seeded into the feed: the alternative
    /// would be a test of the Hub against itself. The sender is resumed
    /// afterwards so the Host is left as it was found.
    /// </remarks>
    [LiveHostFact]
    public async Task When_a_feed_is_offered_a_real_change_reaches_the_console()
    {
        await using var feed = new TrafficFeed(HostAddress(), new EnvironmentApiKeyProvider());

        var state = await feed.StartAsync(TestTimeout());

        if (state != TrafficFeedState.Live)
        {
            // The other half of the contract, and not a skip: this run's Host
            // has no feed, which the test above asserts is an honest answer.
            return;
        }

        var notices = new ConcurrentQueue<TrafficNotice>();
        feed.NoticeReceived += notices.Enqueue;

        var client = Client();

        try
        {
            await client.PauseSenderAsync(HarnessPrincipal, "live-traffic test", TestTimeout());

            var notice = await WaitForNotice(
                notices,
                n => n.Recognised == TrafficNoticeKind.SenderControlChanged);

            Assert.NotNull(notice);
            Assert.Equal(HarnessPrincipal, notice.SubjectId);
        }
        finally
        {
            await client.ResumeSenderAsync(HarnessPrincipal, "live-traffic test cleanup", TestTimeout());
        }
    }

    /// <summary>
    /// Waits for a matching notice, or gives up and returns null.
    /// </summary>
    /// <remarks>
    /// Notice rather than assertion so the caller states what it expected. The
    /// budget is generous because this is a socket round trip across two
    /// processes, and a short one would report a slow machine as a broken feed.
    /// </remarks>
    private static async Task<TrafficNotice?> WaitForNotice(
        ConcurrentQueue<TrafficNotice> notices,
        Func<TrafficNotice, bool> match)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (notices.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(100, TestTimeout());
        }

        return null;
    }

    /// <summary>A token that fails the test rather than hanging it.</summary>
    /// <remarks>
    /// A feed that never answers would otherwise hang the suite until the
    /// runner's own timeout, which reports nothing useful about which call
    /// stalled.
    /// </remarks>
    private static CancellationToken TestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
