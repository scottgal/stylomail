using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// Leases and the recovery sweep: a crash mid-delivery must never strand a message, and must never
/// be quietly reinterpreted as a success.
/// </summary>
public class QueueLeaseAndRecoveryTests
{
    [Fact]
    public async Task A_lease_held_by_a_dead_worker_is_reclaimed_once_it_expires()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            LeaseDuration = TimeSpan.FromMinutes(5),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var first = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(first);

        // A live lease is not stealable.
        Assert.Null(await h.Store.ClaimNextAsync("worker-b"));

        h.Clock.AdvanceMinutes(6);
        var recovery = await h.Store.RecoverAsync();

        Assert.Contains(queueId, recovery.ReclaimedLeases);

        // The ambiguity is recorded rather than glossed over: we do not know whether worker-a
        // delivered the message before it died.
        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.LeaseExpired, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous);

        // And the message is deliverable again rather than stranded forever.
        var second = await h.Store.ClaimNextAsync("worker-b");
        Assert.NotNull(second);
        Assert.Equal(queueId, second!.QueueId);
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_has_its_report_recorded_but_not_applied()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            LeaseDuration = TimeSpan.FromMinutes(5),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var stale = await h.Store.ClaimNextAsync("worker-a");
        h.Clock.AdvanceMinutes(6);
        await h.Store.RecoverAsync();

        var fresh = await h.Store.ClaimNextAsync("worker-b");
        Assert.NotNull(fresh);

        // The dead worker's report turns up late. It may well be true — we cannot tell, and
        // discarding it would destroy the only evidence that it delivered anything.
        var late = await h.Store.CompleteAsync(
            stale!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        Assert.Equal(QueueCompletionStatus.LeaseNotHeld, late.Status);
        Assert.Equal(1, late.AttemptsRecorded);

        var item = await h.Store.GetItemAsync(queueId);

        // It is in the history...
        Assert.Contains(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.WorkerId == "worker-a" && a.Outcome == DeliveryAttemptOutcome.Delivered);

        // ...but it did not overwrite the state of the worker that actually holds the lease.
        Assert.Equal(DeliveryState.Delivering, item!.State);
        Assert.Equal(DeliveryState.Queued, QueueHarness.By(item, "rcpt@example.test").State);
    }

    [Fact]
    public async Task A_slow_worker_still_owns_its_outcome_if_nobody_reclaimed_the_item()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            LeaseDuration = TimeSpan.FromMinutes(5),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");
        h.Clock.AdvanceMinutes(6);

        // The window has closed but no sweep has run, so nobody else has taken the item. The
        // worker is slow, not dead, and it has real evidence: applying it avoids a duplicate
        // delivery that discarding it would guarantee.
        var result = await h.Store.CompleteAsync(
            lease!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        Assert.Equal(QueueCompletionStatus.Applied, result.Status);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By((await h.Store.GetItemAsync(queueId))!, "rcpt@example.test").State);
    }

    [Fact]
    public async Task A_worker_cannot_complete_under_a_lease_it_never_held()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");

        var impersonated = await h.Store.CompleteAsync(
            lease! with { WorkerId = "worker-b" },
            QueueHarness.Delivered("worker-b", "rcpt@example.test"));

        Assert.Equal(QueueCompletionStatus.LeaseNotHeld, impersonated.Status);
        Assert.Equal(DeliveryState.Queued, QueueHarness.By((await h.Store.GetItemAsync(queueId))!, "rcpt@example.test").State);
    }

    [Fact]
    public async Task A_message_is_given_up_on_at_its_configured_expiry()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            RetryExpiry = TimeSpan.FromMinutes(30),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        h.Clock.AdvanceMinutes(31);
        var recovery = await h.Store.RecoverAsync();

        Assert.Contains(queueId, recovery.ExpiredItems);

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.TerminalFailure, item!.State);
        Assert.Equal(DeliveryState.TerminalFailure, QueueHarness.By(item, "rcpt@example.test").State);

        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.Expired, attempt.Outcome);
    }

    [Fact]
    public async Task An_expired_message_is_not_delivered_as_a_last_gasp()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            RetryExpiry = TimeSpan.FromMinutes(30),
        });

        await h.AcceptAsync(QueueHarness.Submission());

        // Past the deadline but before any sweep has run. It must not be claimable: delivering a
        // message whose lifetime has passed is the one thing the retry policy exists to prevent.
        h.Clock.AdvanceMinutes(31);
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));
    }

    [Fact]
    public async Task A_message_that_has_taken_too_many_hops_is_refused_before_acceptance()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, MaxHops = 3 });

        var refused = await h.Store.AcceptAsync(QueueHarness.Submission(hopCount: 3));

        Assert.Equal(QueueAdmission.RefusedLoopLimit, refused.Admission);
        Assert.False(refused.IsAccepted);
        Assert.Empty(await h.Store.CountByStateAsync());

        // Refused *before* acceptance, so not even a payload was written.
        Assert.Empty(Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_unobserved_hop_count_is_accepted_and_recorded_as_unobserved()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, MaxHops = 3 });

        // null means nobody looked. Accepting is the only workable answer — refusing would reject
        // every message until the whole ingress→assess chain populates the field — but the absence
        // must survive to the row. Storing 0 would claim we looked and found no prior hops, which is
        // how the backstop came to read as enforced while never firing.
        var queueId = await h.AcceptAsync(QueueHarness.Submission(hopCount: null));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Null(item!.HopCount);

        // And it is survivable end to end: a null count must not break the read path or recovery.
        // (Reading it back as a non-nullable int would throw here rather than in production.)
        Assert.Empty((await h.Store.RecoverAsync()).HopLimitExceededItems);
    }

    [Fact]
    public async Task An_unobserved_hop_count_is_never_treated_as_zero()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, MaxHops = 1 });

        // With MaxHops = 1, an observed 0 is below the limit and an observed 1 is at it. If null
        // were collapsed to 0 anywhere, this pair would be indistinguishable — the whole point of
        // the nullable field is that they are not.
        var observedZero = await h.AcceptAsync(QueueHarness.Submission(hopCount: 0));
        var unobserved = await h.AcceptAsync(QueueHarness.Submission(hopCount: null));

        Assert.Equal(0, (await h.Store.GetItemAsync(observedZero))!.HopCount);
        Assert.Null((await h.Store.GetItemAsync(unobserved))!.HopCount);
    }

    [Fact]
    public async Task A_message_below_the_hop_limit_is_accepted_and_keeps_its_hop_count()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, MaxHops = 3 });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(hopCount: 2));

        Assert.Equal(2, (await h.Store.GetItemAsync(queueId))!.HopCount);
    }

    [Fact]
    public async Task Lowering_the_hop_limit_stops_messages_already_in_the_queue()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, MaxHops = 10 });
        var queueId = await h.AcceptAsync(QueueHarness.Submission(hopCount: 5));

        // The operator tightens the bound. Messages already stored still have to respect it.
        var stricter = h.Reopen(c => new QueueOptions { TimeProvider = c, MaxHops = 3 });
        var recovery = await stricter.RecoverAsync();

        Assert.Contains(queueId, recovery.HopLimitExceededItems);

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.TerminalFailure, item!.State);
        Assert.Contains(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.HopLimitExceeded);
    }

    [Fact]
    public async Task Reclaiming_a_lease_does_not_punish_recipients_we_never_observed()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            LeaseDuration = TimeSpan.FromMinutes(5),
            MaxAttemptsPerRecipient = 2,
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        // A worker that dies before attempting anything should not consume the delivery budget:
        // nothing was attempted, so nothing can honestly be counted. The message lifetime, not the
        // per-recipient counter, is what bounds a crash loop.
        for (var i = 0; i < 5; i++)
        {
            var lease = await h.Store.ClaimNextAsync("worker-a");
            Assert.NotNull(lease);
            h.Clock.AdvanceMinutes(6);
            await h.Store.RecoverAsync();
        }

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(0, QueueHarness.By(item!, "rcpt@example.test").Attempts);
        Assert.Equal(5, item!.Attempts);   // recorded against the message, not the recipient
        Assert.Equal(DeliveryState.Queued, item.State);
    }

    [Fact]
    public async Task Recovery_is_idempotent_when_there_is_nothing_to_do()
    {
        using var h = new QueueHarness();
        await h.AcceptAsync(QueueHarness.Submission());

        var first = await h.Store.RecoverAsync();
        var second = await h.Store.RecoverAsync();

        Assert.Empty(first.ReclaimedLeases);
        Assert.Empty(first.ExpiredItems);
        Assert.Empty(second.OrphanPayloads);
        Assert.Equal(first.ReclaimedLeases, second.ReclaimedLeases);
        Assert.Empty(second.MissingPayloads);
    }
}
