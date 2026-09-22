using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The delivery-port contract is shared with another project, so it is pinned by tests rather than
/// by two teams remembering the same conversation.
/// </summary>
public class QueueDeliveryPortContractTests
{
    [Fact]
    public async Task The_documented_port_outcomes_are_exactly_the_ones_the_store_accepts()
    {
        // The published vocabulary and the enforced one must be the same set. Without this, adding
        // an enum member or tightening the check would silently make the shared documentation a lie,
        // and the failure would surface as an ArgumentException in someone else's worker.
        foreach (var outcome in Enum.GetValues<DeliveryAttemptOutcome>())
        {
            using var h = new QueueHarness();
            await h.AcceptAsync(QueueHarness.Submission());

            var lease = await h.Store.ClaimNextAsync("worker-a");
            Assert.NotNull(lease);

            var report = new DeliveryReport
            {
                WorkerId = "worker-a",
                Recipients = [new() { Recipient = "rcpt@example.test", Outcome = outcome }],
            };

            if (DeliveryPortContract.ReportableOutcomes.Contains(outcome))
            {
                var result = await h.Store.CompleteAsync(lease!, report);
                Assert.Equal(QueueCompletionStatus.Applied, result.Status);
            }
            else
            {
                await Assert.ThrowsAsync<ArgumentException>(() => h.Store.CompleteAsync(lease!, report));
            }
        }
    }

    [Fact]
    public async Task In_doubt_from_a_port_is_accepted_and_does_not_settle_the_recipient()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        var lease = await h.Store.ClaimNextAsync("worker-a");

        // Exactly the state a transport reaches when it sent DATA and the connection died before
        // the 250 arrived.
        var portResult = new DeliveryPortResult
        {
            Recipients =
            [
                new()
                {
                    Recipient = "rcpt@example.test",
                    Outcome = DeliveryAttemptOutcome.InDoubt,
                    Detail = "connection reset after DATA; no acknowledgement observed",
                },
            ],
        };

        var result = await h.Store.CompleteAsync(lease!, portResult.AsReport("worker-a"));

        Assert.Equal(QueueCompletionStatus.Applied, result.Status);

        var recipient = QueueHarness.By((await h.Store.GetItemAsync(queueId))!, "rcpt@example.test");

        // Reachable, retried, and not treated as a settle.
        Assert.Equal(DeliveryState.RetryScheduled, recipient.State);
        Assert.Null(recipient.DeliveredAt);

        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.InDoubt, attempt.Outcome);
        Assert.True(attempt.IsAmbiguous);
    }

    [Fact]
    public void A_port_result_adapts_to_a_report_without_translation()
    {
        var recipients = new List<RecipientDeliveryResult>
        {
            new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
            new()
            {
                Recipient = "b@example.test",
                Outcome = DeliveryAttemptOutcome.TemporaryFailure,
                Detail = "421 busy",
            },
        };

        var report = new DeliveryPortResult { Recipients = recipients, Detail = "batch 1" }.AsReport("worker-a");

        // Same list instance, not a copy that could drift from it.
        Assert.Same(recipients, report.Recipients);
        Assert.Equal("worker-a", report.WorkerId);
        Assert.Equal("batch 1", report.Detail);
    }

    [Fact]
    public async Task A_partial_port_result_retries_only_what_it_reported_as_pending()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            recipients: ["a@example.test", "b@example.test", "c@example.test"]));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.Equal(3, lease!.PendingRecipients.Count);

        // A transport that delivered one, hit a 4xx on another and a 5xx on the third.
        await h.Store.CompleteAsync(lease, new DeliveryPortResult
        {
            Recipients =
            [
                new() { Recipient = "a@example.test", Outcome = DeliveryAttemptOutcome.Delivered },
                new() { Recipient = "b@example.test", Outcome = DeliveryAttemptOutcome.TemporaryFailure },
                new() { Recipient = "c@example.test", Outcome = DeliveryAttemptOutcome.PermanentFailure },
            ],
        }.AsReport("worker-a"));

        h.Clock.AdvanceMinutes(30);
        var retry = await h.Store.ClaimNextAsync("worker-a");

        Assert.Equal("b@example.test", Assert.Single(retry!.PendingRecipients).Recipient);
        Assert.Equal(QueueItemOutcome.Pending, (await h.Store.GetItemAsync(queueId))!.Outcome);
    }
}
