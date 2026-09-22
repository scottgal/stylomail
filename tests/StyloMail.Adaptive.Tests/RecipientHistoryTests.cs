using StyloMail.Adaptive.Profiles;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// Bounded recipient tracking: who a sender has addressed, and whether we can still say.
/// </summary>
/// <remarks>
/// An account fanning out to strangers is the compromised-account shape this system exists to
/// catch, and "distinct recipients" is the measurement that names it.
///
/// <para>
/// <b>A capped set must not lie.</b> When the set saturates the count is a floor rather than a
/// measurement, and, the part that matters more, <b>a recipient absent from a full set may be
/// absent because it was evicted</b>. "Not in the set" stops meaning "never seen", so novelty
/// becomes <em>unknown</em> rather than <em>novel</em>. Reporting it as novel would manufacture the
/// single most alarming signal in the profile out of a memory bound, and that signal is the one
/// most likely to lead to an irreversible action.
/// </para>
/// </remarks>
public class RecipientHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    [Fact]
    public void DistinctRecipientsAreCountedOverTheWindow()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["a", "b", "c"], Start);
        history.Record(["c", "d"], Start.AddHours(1));

        Assert.Equal(4, history.DistinctSince(Start.AddHours(1) - Month));
        Assert.False(history.Truncated);
    }

    [Fact]
    public void RepeatedlyAddressingOneRecipientIsNotFanOut()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        for (var i = 0; i < 50; i++)
        {
            history.Record(["a"], Start.AddMinutes(i));
        }

        // Fifty messages to one recipient. Counting addresses rather than recipients would call
        // this fan-out, which is the whole reason distinct-ness has to be tracked separately.
        Assert.Equal(1, history.DistinctSince(Start));
    }

    [Fact]
    public void AsenderFanningOutIsCountedExactlyWhileTheSetHasRoom()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record([.. Enumerable.Range(0, 100).Select(i => $"recipient-{i}")], Start);

        Assert.Equal(100, history.DistinctSince(Start));
        Assert.False(history.Truncated);
    }

    [Fact]
    public void ARecipientLastAddressedOutsideTheWindowStopsCounting()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["a"], Start);
        history.Record(["b"], Start.AddDays(31));

        Assert.Equal(1, history.DistinctSince(Start.AddDays(31) - Month));
    }

    [Fact]
    public void ASaturatedSetSaysSoRatherThanReportingTheCapAsAMeasurement()
    {
        var history = new RecipientHistory(capacity: 4, Month);

        history.Record(["a", "b", "c", "d"], Start);
        history.Record(["e"], Start.AddMinutes(1));

        // Five distinct recipients against a set that holds four. Reporting "4" as though it were
        // the answer is the lie this flag exists to prevent.
        Assert.True(history.Truncated);
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public void NoveltyIsStillAnsweredOnceTheSetHasSaturated()
    {
        var history = new RecipientHistory(capacity: 4, Month);
        history.Record(["a", "b", "c", "d"], Start);

        Assert.Equal(1, history.NovelCount(["a", "z"]));

        history.Record(["e", "f"], Start.AddMinutes(1));   // saturates the distinct set

        // The capped set has turned recipients away, so it can no longer count distinct-ness
        // exactly. The membership filter never forgets, so the question novelty actually asks is
        // still answerable: which is the entire reason for keeping two structures.
        Assert.True(history.Truncated);
        Assert.Equal(1, history.NovelCount(["e", "z"]));
        Assert.Equal(0, history.NovelCount(["a", "b"]));
    }

    [Fact]
    public void NoveltyStillRemembersARecipientTheDistinctSetLetGo()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["a"], Start);
        history.Record(["b"], Start.AddDays(31));   // ages "a" out of the distinct set

        // "a" was addressed. The distinct set released it because it is no longer recent, but the
        // membership filter remembers it was ever seen, and "have we ever seen them" is the
        // question novelty asks. Answering "never" here would be a false alarm about a known
        // correspondent.
        Assert.True(history.Truncated);
        Assert.Equal(0, history.NovelCount(["a"]));
        Assert.Equal(1, history.NovelCount(["brand-new"]));
    }

    [Fact]
    public void NoveltyKeepsAnsweringForTheSendersTheCappedSetGivesUpOn()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["dormant"], Start);
        history.Record(["active"], Start.AddDays(31));   // ages "dormant" out: truncated for good

        for (var i = 0; i < 40; i++)
        {
            history.Record(["active"], Start.AddDays(31).AddHours(i));
        }

        // This is the case the whole design turns on. An established sender: more than the
        // capacity, or merely some recipients dormant for a month: permanently loses *exact
        // distinct counting*, and that is honest. Losing novelty as well would take the signal
        // dark precisely on the accounts with the widest reach, which are the ones most likely to
        // be compromised, and a signal that vanishes exactly where it is needed is worse than no
        // signal, because it still looks present.
        Assert.True(history.Truncated);
        Assert.Equal(1, history.NovelCount(["brand-new"]));
        Assert.Equal(0, history.NovelCount(["dormant"]));
    }

    [Fact]
    public void NoveltyIsUnknownWhenTheHistoryDoesNotCoverThePrincipalsPast()
    {
        var history = new RecipientHistory();
        history.Record(["alice"], Start);

        Assert.Equal(1, history.NovelCount(["stranger"]));

        // A history restored from storage covers the principal's past; one silently starting
        // part-way through their life does not. Reading "not present" from the latter would report
        // every recipient as novel: manufacturing the alarm this type exists to prevent.
        history.MarkIncomplete();

        Assert.Null(history.NovelCount(["stranger"]));
        Assert.Null(history.NovelCount(["alice"]));
    }

    [Fact]
    public void ARestoredHistoryKeepsAnsweringBothQuestions()
    {
        var original = new RecipientHistory(capacity: 256, Month);
        original.Record(["a", "b"], Start);
        original.Record(["c"], Start.AddDays(40));   // ages a and b out, truncating

        var restored = RecipientHistory.Restore(
            capacity: 256,
            window: Month,
            entries: original.Entries,
            seen: RecipientBloomFilter.FromBytes(original.Seen.ToBytes()),
            truncated: original.Truncated);

        Assert.True(restored.Truncated);
        Assert.Equal(1, restored.DistinctSince(Start.AddDays(40) - Month));
        Assert.Equal(1, restored.NovelCount(["brand-new"]));
        Assert.Equal(0, restored.NovelCount(["a"]));
    }

    [Fact]
    public void ANeverTruncatedHistoryCanStillAnswerNovelty()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["a", "b"], Start);

        Assert.Equal(2, history.NovelCount(["x", "y"]));
        Assert.Equal(0, history.NovelCount(["a", "b"]));
        Assert.Equal(1, history.NovelCount(["a", "brand-new"]));
    }

    [Fact]
    public void TheSetIsBoundedByConstruction()
    {
        var history = new RecipientHistory(capacity: 8, Month);

        for (var i = 0; i < 500; i++)
        {
            history.Record([$"recipient-{i}"], Start.AddSeconds(i));
        }

        Assert.True(history.Count <= 8);
        Assert.True(history.Truncated);
    }

    [Fact]
    public void ReAddressingARecipientAlreadyHeldDoesNotSaturate()
    {
        var history = new RecipientHistory(capacity: 3, Month);

        for (var i = 0; i < 100; i++)
        {
            history.Record(["a", "b", "c"], Start.AddSeconds(i));
        }

        // The set is full but nothing was ever turned away, so it is still complete for these
        // three recipients and novelty remains answerable.
        Assert.False(history.Truncated);
        Assert.Equal(1, history.NovelCount(["new-recipient"]));
    }

    [Fact]
    public void DistinctInTheLastHourIsSeparateFromDistinctInTheLastMonth()
    {
        var history = new RecipientHistory(capacity: 256, Month);

        history.Record(["old-1", "old-2"], Start);
        history.Record(["new-1"], Start.AddDays(10));

        var now = Start.AddDays(10).AddMinutes(5);

        Assert.Equal(1, history.DistinctSince(now - TimeSpan.FromHours(1)));
        Assert.Equal(3, history.DistinctSince(now - Month));
    }
}
