using StyloMail.Adaptive.Learning;

namespace StyloMail.Adaptive.Tests;

/// <summary>
/// The recipient budget: what it bounds, and over what period.
/// </summary>
/// <remarks>
/// The budget exists to bound <b>escape volume</b>, how much a compromised principal can send
/// between the compromise starting and the system noticing. That is a property of a window. A
/// lifetime cap is uncorrelated with it: a sender's 501st recipient is not more dangerous than
/// their 5th, and treating it as though it were permanently blocks the account for sending
/// legitimate mail, which is worse than not bounding anything at all.
///
/// <para>
/// <c>Release</c> returns a value rather than being a void "and it's fine now" call, because a
/// release that quietly returns less than asked is a caller's books diverging from the ledger's
/// with nothing to notice. The shortfall is handed back so it can be accounted for.
/// </para>
/// </remarks>
public class QuotaLedgerTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly TestClock _clock = TestClock.AtEpoch();

    [Fact]
    public void TheFullBudgetIsAvailableAgainOnceTheWindowElapses()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));
        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));

        _clock.Advance(Hour + TimeSpan.FromSeconds(1));

        // A lifetime cap never reopens, which is what made a single newsletter an irreversible
        // account stop. A window is the whole point.
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));
    }

    [Fact]
    public void TheBudgetDoesNotReopenBeforeTheWindowElapses()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));

        _clock.Advance(Hour - TimeSpan.FromSeconds(1));

        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
        Assert.False(ledger.TryReserve("tenant-a", "sender", 1, _clock.GetUtcNow()));
    }

    [Fact]
    public void AReservationStopsCountingExactlyWhenItsWindowCloses()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));

        // The boundary, pinned. A reservation made at t counts for [t, t + window): at exactly
        // t + window it is gone. Off-by-one here is invisible in ordinary traffic and shows up
        // as a budget that is either permanently one reservation short or briefly over-generous.
        _clock.Advance(Hour - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void OnlyReservationsPastTheWindowAreDropped()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);

        Assert.True(ledger.TryReserve("tenant-a", "sender", 60, _clock.GetUtcNow()));   // t = 0
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(ledger.TryReserve("tenant-a", "sender", 40, _clock.GetUtcNow()));   // t = 30m
        Assert.Equal(0, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(31));                   // t = 61m: only the first expired

        // Dropping the wrong side of the window is the mutation that matters here. Expiring
        // everything would hand back the whole budget; expiring nothing would keep it shut.
        // Exactly the second reservation's forty recipients are still counted.
        Assert.Equal(60, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void AnOutOfOrderInstantStillExpiresItsReservation()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 200, Hour);
        var capturedEarlier = _clock.GetUtcNow();          // t = 0

        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(ledger.TryReserve("tenant-a", "sender", 60, _clock.GetUtcNow()));  // recorded first
        Assert.True(ledger.TryReserve("tenant-a", "sender", 60, capturedEarlier));     // recorded second

        _clock.Advance(TimeSpan.FromMinutes(31));          // t = 61m

        // Nobody sorts these. Each caller supplies its own instant, so a slow concurrent
        // assessment can record a reservation it captured thirty minutes ago *after* a faster
        // one recorded a more recent entry, and the list is then not in time order.
        //
        // Pruning must therefore scan the whole list. Stopping at the first live entry would
        // leave the expired reservation sitting behind a live one, counted forever, and the
        // budget would be quietly smaller than the window says. Every instant here is stated by
        // the caller; the ledger has no clock and cannot reorder them.
        Assert.Equal(140, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void AnInstantOlderThanTheWindowReservesNothingIntoTheFuture()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        var stale = _clock.GetUtcNow();

        _clock.Advance(Hour + TimeSpan.FromMinutes(1));
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, stale));

        // The reservation is dated back beyond the window, so it is already expired at the
        // instant it is checked against. It must not appear to hold budget now.
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void ReservationsAreScopedPerPrincipalAndTenant()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);

        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));

        Assert.Equal(100, ledger.Remaining("tenant-a", "other-sender", _clock.GetUtcNow()));
        Assert.Equal(100, ledger.Remaining("tenant-b", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void ReleaseUndoesTheMostRecentReservationsFirst()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 200, Hour);

        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));  // t = 0
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));  // t = 30m

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(100, ledger.Release("tenant-a", "sender", 100, _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(30));                   // t = 61m

        // The surviving reservation is the one made at t = 0, so it has now expired and the
        // full budget is back. Had the release taken the *oldest* entry instead, the survivor
        // would be the t = 30m one and only half the budget would be available.
        //
        // Releasing the most recent entries is the right order because this undoes a
        // reservation that was just made and did not lead to dispatch, and it returns the
        // budget for the longest remaining part of the window, which is what "never dispatched"
        // should mean. It is pinned here rather than left to whichever end of the list is handy.
        Assert.Equal(200, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void ReleaseCannotGiveBackBudgetThatHasAlreadyExpired()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 100, _clock.GetUtcNow()));

        _clock.Advance(Hour + TimeSpan.FromMinutes(1));

        // Nothing is outstanding any more: the budget reopened on its own. Reporting a release
        // here would be handing back capacity that was never taken.
        Assert.Equal(0, ledger.Release("tenant-a", "sender", 100, _clock.GetUtcNow()));
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void ReleaseReturnsTheAmountActuallyReturned()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 40, _clock.GetUtcNow()));

        Assert.Equal(40, ledger.Release("tenant-a", "sender", 40, _clock.GetUtcNow()));
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void AnOverReleaseReturnsOnlyWhatWasActuallyReturned()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 30, _clock.GetUtcNow()));

        // Fifty asked for, thirty ever reserved. A legitimate double-release on a retry path
        // must not crash, so this clamps rather than throws, but the caller is told.
        Assert.Equal(30, ledger.Release("tenant-a", "sender", 50, _clock.GetUtcNow()));
    }

    [Fact]
    public void ClampingNeverManufacturesHeadroom()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 30, _clock.GetUtcNow()));

        ledger.Release("tenant-a", "sender", 5_000, _clock.GetUtcNow());

        // The budget may never exceed its capacity, however much a caller claims to give back.
        Assert.Equal(100, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
        Assert.Equal(100, ledger.Capacity);
    }

    [Fact]
    public void TheReturnedAmountIsExactlyTheChangeInRemainingBudget()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 25, _clock.GetUtcNow()));

        var before = ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow());
        var released = ledger.Release("tenant-a", "sender", 999, _clock.GetUtcNow());
        var after = ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow());

        // The invariant a caller can rely on without knowing the internal state: what came back
        // is what actually moved. If these two ever disagree, the returned figure is a claim
        // rather than a fact, and the divergence it exists to expose is hidden again.
        Assert.Equal(after - before, released);
        Assert.Equal(25, released);
    }

    [Fact]
    public void ASuccessfulReleaseIsFullyReported()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);
        Assert.True(ledger.TryReserve("tenant-a", "sender", 10, _clock.GetUtcNow()));

        var released = ledger.Release("tenant-a", "sender", 4, _clock.GetUtcNow());

        Assert.Equal(4, released);
        Assert.Equal(94, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }

    [Fact]
    public void AReservationSpanningTheWindowBoundaryExpiresByTheClockNotByTheMessageCount()
    {
        var ledger = new SendingQuotaLedger(recipientsPerWindow: 100, Hour);

        // One recipient at a time, every twenty minutes: five calls, still inside the window
        // for the first of them. Counting messages rather than time would call this "five
        // reservations" and reopen nothing.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(ledger.TryReserve("tenant-a", "sender", 20, _clock.GetUtcNow()));
            _clock.Advance(TimeSpan.FromMinutes(20));
        }

        // t = 100m. The t = 0, 20m and 40m reservations are all past the window; the two made
        // at 60m and 80m are not.
        Assert.Equal(60, ledger.Remaining("tenant-a", "sender", _clock.GetUtcNow()));
    }
}
