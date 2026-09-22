using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// Per-recipient delivery state, bounded retry, and the ambiguity that SMTP forces on us.
/// </summary>
public class QueueRecipientAndRetryTests
{
    [Fact]
    public async Task A_multi_recipient_message_retries_only_the_recipients_still_pending()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            recipients: ["a@example.test", "b@example.test", "c@example.test"]));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.Equal(3, lease!.PendingRecipients.Count);

        await h.Store.CompleteAsync(lease, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
                new()
                {
                    Recipient = "b@example.test",
                    Outcome = DeliveryAttemptOutcome.TemporaryFailure,
                    Detail = "421 upstream busy",
                },
                new()
                {
                    Recipient = "c@example.test",
                    Outcome = DeliveryAttemptOutcome.PermanentFailure,
                    Detail = "550 no such user",
                },
            ],
        });

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(item!, "a@example.test").State);
        Assert.Equal(DeliveryState.RetryScheduled, QueueHarness.By(item!, "b@example.test").State);
        Assert.Equal(DeliveryState.TerminalFailure, QueueHarness.By(item!, "c@example.test").State);

        // The transaction is not over, and the split is visible rather than collapsed into a
        // single verdict for the message.
        Assert.Equal(QueueItemOutcome.Pending, item!.Outcome);

        // The retry must name the pending recipient and nobody else: not the one that succeeded,
        // and not the one that is finished.
        h.Clock.AdvanceMinutes(30);
        var retry = await h.Store.ClaimNextAsync("worker-a");
        var pending = Assert.Single(retry!.PendingRecipients);
        Assert.Equal("b@example.test", pending.Recipient);

        await h.Store.CompleteAsync(retry, QueueHarness.Delivered("worker-a", "b@example.test"));

        var settled = await h.Store.GetItemAsync(queueId);

        // Two delivered, one failed forever. Reporting this as a plain success or a plain failure
        // would each hide half of what happened.
        Assert.Equal(QueueItemOutcome.PartiallyDelivered, settled!.Outcome);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(settled, "a@example.test").State);
        Assert.Equal(DeliveryState.Delivered, QueueHarness.By(settled, "b@example.test").State);
        Assert.Equal(DeliveryState.TerminalFailure, QueueHarness.By(settled, "c@example.test").State);
    }

    [Fact]
    public async Task Each_recipient_carries_its_own_retry_schedule()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(10),
            MaxBackoff = TimeSpan.FromMinutes(10),
            BackoffJitterFraction = 0,
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            recipients: ["a@example.test", "b@example.test"]));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new()
                {
                    Recipient = "a@example.test",
                    Outcome = DeliveryAttemptOutcome.TemporaryFailure,
                    Detail = "421 upstream busy",
                },
            ],
        });

        var item = await h.Store.GetItemAsync(queueId);
        var a = QueueHarness.By(item!, "a@example.test");
        var b = QueueHarness.By(item!, "b@example.test");

        // The recipient that failed backs off on its own account...
        Assert.Equal(DeliveryState.RetryScheduled, a.State);
        Assert.Equal(1, a.Attempts);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromMinutes(10), a.NextAttemptAt);

        // ...and the one that was never attempted is untouched, because it was never due.
        Assert.Equal(DeliveryState.Queued, b.State);
        Assert.Equal(0, b.Attempts);

        // The message is therefore due immediately for 'b'. A single message-level timer would have
        // held 'b' back for ten minutes behind a failure that had nothing to do with it.
        Assert.Equal(DeliveryState.Queued, item!.State);

        var retry = await h.Store.ClaimNextAsync("worker-a");
        var pending = Assert.Single(retry!.PendingRecipients);
        Assert.Equal("b@example.test", pending.Recipient);
    }

    [Fact]
    public async Task Retry_backoff_grows_monotonically_and_is_capped()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(30),
            BackoffJitterFraction = 0,
        });

        var delays = Enumerable.Range(1, 14).Select(attempt => h.Store.Backoff("q-fixed", attempt)).ToList();

        Assert.True(delays[1] > delays[0], "Backoff should grow.");
        Assert.True(delays[^1] > delays[4], "Backoff should keep growing before the cap.");

        for (var i = 1; i < delays.Count; i++)
        {
            Assert.True(delays[i] >= delays[i - 1], $"Backoff went backwards at attempt {i + 1}.");
        }

        // Bounded: an unbounded backoff is a message that is never delivered and never given up on.
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(30), delays[^1]);
    }

    [Fact]
    public async Task Jitter_spreads_items_without_making_the_schedule_unpredictable()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(10),
            MaxBackoff = TimeSpan.FromHours(4),
            BackoffJitterFraction = 0.2,
        });

        var schedules = Enumerable.Range(0, 40)
            .Select(i => h.Store.Backoff($"q-{i}", 1))
            .ToList();

        // Spread out...
        Assert.True(schedules.Distinct().Count() > 20, "Jitter should de-synchronise items.");

        // ...but deterministic: the same item always gets the same schedule, which is what makes
        // it safe to recover an item and recompute rather than persist the decision.
        Assert.Equal(schedules, Enumerable.Range(0, 40).Select(i => h.Store.Backoff($"q-{i}", 1)));

        // ...and still inside the intended ±20% band around the base delay.
        Assert.All(schedules, d => Assert.InRange(d, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_recipient_ends_in_terminal_failure()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(10),
            MaxAttemptsPerRecipient = 3,
            RetryExpiry = TimeSpan.FromDays(7),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var claimed = 0;
        QueueLease? lease;
        while ((lease = await h.Store.ClaimNextAsync("worker-a")) is not null)
        {
            claimed++;
            Assert.True(claimed < 20, "Retries must be bounded.");
            await h.Store.CompleteAsync(lease, QueueHarness.TemporaryFailure("worker-a", "rcpt@example.test"));
            h.Clock.AdvanceMinutes(30);
        }

        Assert.Equal(3, claimed);

        var item = await h.Store.GetItemAsync(queueId);
        var recipient = QueueHarness.By(item!, "rcpt@example.test");
        Assert.Equal(DeliveryState.TerminalFailure, item!.State);
        Assert.Equal(DeliveryState.TerminalFailure, recipient.State);
        Assert.Equal(3, recipient.Attempts);

        // TerminalFailure is reachable several ways, so "it is terminal" does not by itself show
        // *which* bound was hit. Naming the mechanism is what separates exhaustion from the
        // lifetime expiry, the other path that produces this same state from a retry failure.
        Assert.NotNull(recipient.LastError);
        var reason = recipient.LastError!;
        Assert.Contains("limit", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lifetime", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_in_doubt_delivery_is_retried_and_the_ambiguity_is_preserved()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, BaseBackoff = TimeSpan.FromMinutes(1) });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new()
                {
                    Recipient = "rcpt@example.test",
                    Outcome = DeliveryAttemptOutcome.InDoubt,
                    Detail = "connection dropped after DATA; no acknowledgement seen",
                },
            ],
        });

        // We retry, a duplicate is recoverable and a silent loss is not, but the record says
        // plainly that the outcome is unknown. Nothing here claims Message-ID deduplication has
        // made this safe.
        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.RetryScheduled, QueueHarness.By(item!, "rcpt@example.test").State);

        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous);
    }

    [Fact]
    public async Task Attempt_history_is_append_only_across_retries()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(10),
            MaxAttemptsPerRecipient = 3,
            RetryExpiry = TimeSpan.FromDays(7),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var snapshots = new List<IReadOnlyList<QueueAttempt>>();
        for (var i = 0; i < 3; i++)
        {
            var lease = await h.Store.ClaimNextAsync("worker-a");
            Assert.NotNull(lease);
            await h.Store.CompleteAsync(lease!, QueueHarness.TemporaryFailure("worker-a", "rcpt@example.test"));
            snapshots.Add(await h.Store.GetAttemptsAsync(queueId));
            h.Clock.AdvanceMinutes(30);
        }

        var final = snapshots[^1];
        Assert.Equal(3, final.Count);

        // Every earlier observation is a prefix of the final history, record-for-record. Nothing
        // was rewritten or removed between retries.
        for (var s = 0; s < snapshots.Count; s++)
        {
            Assert.Equal(snapshots[s].Count, s + 1);
            for (var i = 0; i < snapshots[s].Count; i++)
            {
                Assert.Equal(snapshots[s][i], final[i]);
            }
        }

        Assert.Equal(final.Select(a => a.AttemptId).Order(), final.Select(a => a.AttemptId));
        Assert.All(final, a => Assert.Equal(DeliveryAttemptOutcome.TemporaryFailure, a.Outcome));
    }

    [Fact]
    public async Task A_report_for_an_already_settled_recipient_is_recorded_but_never_applied()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            recipients: ["a@example.test", "b@example.test"]));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
                new() { Recipient = "b@example.test", Outcome = DeliveryAttemptOutcome.TemporaryFailure, Detail = "421" },
            ],
        });

        h.Clock.AdvanceMinutes(30);
        var retry = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(retry);

        // A worker that re-reports a recipient it already delivered, with a worse outcome.
        var result = await h.Store.CompleteAsync(retry!, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.InDoubt, Detail = "stale" },
                new() { Recipient = "b@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
            ],
        });

        Assert.Contains("a@example.test", result.SupersededRecipients);

        var item = await h.Store.GetItemAsync(queueId);
        var a = QueueHarness.By(item!, "a@example.test");

        // A settled recipient is not reopened by a later report, however it is described.
        Assert.Equal(DeliveryState.Delivered, a.State);
        Assert.Equal(1, a.Attempts);
        Assert.Equal(QueueItemOutcome.AllDelivered, item!.Outcome);

        // The report still lands in history: it is evidence about what a worker observed.
        Assert.Contains(
            await h.Store.GetAttemptsAsync(queueId),
            x => x.RecipientKey == "a@example.test" && x.Outcome == DeliveryAttemptOutcome.InDoubt);
    }

    [Fact]
    public async Task A_pending_recipient_is_not_attempted_before_its_backoff_elapses()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            BaseBackoff = TimeSpan.FromMinutes(10),
            MaxBackoff = TimeSpan.FromMinutes(10),
            BackoffJitterFraction = 0,
        });

        await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.TemporaryFailure("worker-a", "rcpt@example.test"));

        // Well before the 10-minute backoff elapses.
        h.Clock.AdvanceMinutes(1);
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        h.Clock.AdvanceMinutes(10);
        var retry = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(retry);
        Assert.Single(retry!.PendingRecipients);
    }

    [Fact]
    public async Task A_worker_cannot_report_a_policy_event_as_a_delivery_outcome()
    {
        using var h = new QueueHarness();
        await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.CompleteAsync(lease!, new DeliveryReport
        {
            WorkerId = "worker-a",
            Recipients =
            [
                new()
                {
                    Recipient = "rcpt@example.test",
                    Outcome = DeliveryAttemptOutcome.HoldExpired,
                },
            ],
        }));
    }
}
