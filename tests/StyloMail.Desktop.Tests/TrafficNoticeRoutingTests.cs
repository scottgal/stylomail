using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// What a pushed notice makes the console read.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is a pure function because the window cannot be tested: it is an
/// Avalonia control and needs a display to exist at all. Before this type
/// existed the same mapping was a switch inside the window, where its
/// unrecognised-kind branch could not be driven by anything, from a unit test or
/// from a harness script (no YAML action can post to the Hub). So the branch
/// that decides what happens to a hint from a newer Host was covered by reading
/// it, which is not coverage.
/// </para>
/// <para>
/// The four known kinds are asserted one by one rather than as a set, so a
/// mapping quietly repointed at another read fails here instead of showing up
/// as a pane that refreshes when it should not.
/// </para>
/// </remarks>
public sealed class TrafficNoticeRoutingTests
{
    [Theory]
    [InlineData("ReadinessChanged", TrafficNoticeRoute.Readiness)]
    [InlineData("SenderControlChanged", TrafficNoticeRoute.Senders)]
    [InlineData("DecisionRecorded", TrafficNoticeRoute.Selection)]
    [InlineData("MessageStateChanged", TrafficNoticeRoute.Selection)]
    public void A_known_kind_is_read_back_on_the_surface_it_moved(string wire, TrafficNoticeRoute expected)
        => Assert.Equal(expected, TrafficNoticeRouting.For(new TrafficNotice { Kind = wire }));

    /// <summary>
    /// A kind this build does not know is read back in full rather than dropped.
    /// </summary>
    /// <remarks>
    /// The obligation this test exists for. Dropping the hint is the tempting
    /// answer to a value this build cannot interpret, and it is the wrong one:
    /// the feed then looks live while the screen stops following it, which is
    /// the exact failure the live surface is here to make impossible. The cost of
    /// the other answer is one extra round trip.
    /// </remarks>
    [Fact]
    public void An_unrecognised_kind_is_read_back_across_the_whole_surface()
    {
        var route = TrafficNoticeRouting.For(new TrafficNotice { Kind = "SomethingNewer" });

        Assert.Equal(TrafficNoticeRoute.Everything, route);
    }

    /// <summary>
    /// An unreadable hint does not get to choose which surface is read.
    /// </summary>
    /// <remarks>
    /// The notice carries an id, and an id is a plausible-looking argument for
    /// reading one listing: a newer Host's kind would name a route this build
    /// simply has not been taught. Honouring it would be the console guessing at
    /// what the Host meant and then acting as though it knew, on the surface
    /// most likely to be the wrong one. The id changes nothing, which is what
    /// this asserts: the route is decided by whether the kind was understood and
    /// by nothing else the notice holds.
    /// </remarks>
    [Fact]
    public void An_unrecognised_kind_cannot_pick_the_surface_it_reads()
    {
        var withAnId = new TrafficNotice
        {
            Kind = "SomethingNewer",
            SubjectId = "assessment-1",
            OccurredAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(TrafficNoticeRoute.Everything, TrafficNoticeRouting.For(withAnId));
    }

    [Fact]
    public void A_notice_with_no_kind_at_all_is_read_back_across_the_whole_surface()
        => Assert.Equal(
            TrafficNoticeRoute.Everything,
            TrafficNoticeRouting.For(new TrafficNotice { Kind = null }));

    /// <summary>
    /// An unknown kind is not silently taken for one this build knows.
    /// </summary>
    /// <remarks>
    /// The same hazard <see cref="TrafficNotice.Recognised"/> documents for its
    /// own enum, and the route enum has the same trap in a different place: its
    /// first member is the status bar's refresh, so a mapping that fell through
    /// to a default value would have the console re-read readiness and believe
    /// the Host had said readiness moved.
    /// </remarks>
    [Fact]
    public void An_unrecognised_kind_is_not_routed_as_a_known_one()
    {
        var route = TrafficNoticeRouting.For(new TrafficNotice { Kind = "SomethingNewer" });

        Assert.NotEqual(TrafficNoticeRoute.Readiness, route);
    }
}
