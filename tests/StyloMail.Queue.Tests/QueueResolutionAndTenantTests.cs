using System.Text;
using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The operator-facing surface: tenant-scoped reads, reviewer decisions, and the durable-reference
/// boundary.
/// </summary>
public class QueueResolutionAndTenantTests
{
    [Fact]
    public async Task A_read_is_scoped_to_its_tenant()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(tenantId: "acme"));

        // Give the item real history first, so the history assertion below is not trivially true
        // on a message that has none anyway.
        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        Assert.NotNull(await h.Store.GetItemAsync(queueId, "acme"));
        Assert.NotEmpty(await h.Store.GetAttemptsAsync(queueId, "acme"));

        // Same id, wrong tenant: reads as absent. A queue id is unguessable, but a route that could
        // be pointed at another tenant's mail by editing one path segment is not worth writing.
        Assert.Null(await h.Store.GetItemAsync(queueId, "globex"));
        Assert.Empty(await h.Store.GetAttemptsAsync(queueId, "globex"));
    }

    [Fact]
    public async Task A_submission_can_be_looked_up_by_its_scoped_idempotency_key()
    {
        using var h = new QueueHarness();

        var accepted = await h.Store.AcceptAsync(
            QueueHarness.Submission(tenantId: "acme", idempotencyKey: "k1", mimeDigest: "digest-1"));

        var found = await h.Store.FindSubmissionAsync("acme", "k1");

        Assert.NotNull(found);
        Assert.Equal(accepted.QueueId, found!.QueueId);
        Assert.Equal("digest-1", found.MimeDigest);

        // Keys are tenant-scoped, so another tenant's identical key is invisible.
        Assert.Null(await h.Store.FindSubmissionAsync("globex", "k1"));
        Assert.Null(await h.Store.FindSubmissionAsync("acme", "never-used"));
    }

    [Fact]
    public async Task A_quarantined_message_is_released_for_delivery_by_a_reviewer()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Quarantined));

        // Quarantined mail does not move on its own.
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        Assert.True(await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Release, "reviewer-7"));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);
        Assert.Equal(queueId, lease!.QueueId);

        // The decision is in the trail, with the actor, in order.
        var released = Assert.Single(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.QuarantineReleased);
        Assert.Equal("reviewer-7", released.WorkerId);
    }

    [Fact]
    public async Task A_rejected_quarantine_is_retained_as_a_record_and_never_delivered()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Quarantined));

        Assert.True(await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Reject, "reviewer-7"));

        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.TerminalFailure, QueueHarness.By(item!, "rcpt@example.test").State);

        // Rejection is a decision, not a deletion: the message and its payload stay for the audit.
        Assert.True(h.Spool.Exists(item!.Envelope.PayloadReference));
        Assert.Contains(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.QuarantineRejected && a.WorkerId == "reviewer-7");
    }

    [Fact]
    public async Task Quarantine_resolution_is_scoped_and_idempotent()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(tenantId: "acme", state: DeliveryState.Quarantined));

        Assert.False(await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Release, "r", "globex"));
        Assert.False(await h.Store.ResolveQuarantineAsync("q-does-not-exist", QuarantineResolution.Release, "r"));

        Assert.True(await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Release, "r", "acme"));

        // Nothing is quarantined any more, so a second decision is a no-op rather than a
        // second contradicting entry in the trail.
        Assert.False(await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Reject, "r", "acme"));
        Assert.DoesNotContain(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.QuarantineRejected);
    }

    [Fact]
    public async Task A_worker_cannot_report_a_reviewers_decision_as_its_own()
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
                    Outcome = DeliveryAttemptOutcome.QuarantineReleased,
                },
            ],
        }));
    }

    [Fact]
    public async Task The_stored_payload_reference_is_durable_by_construction()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 128));

        var item = await h.Store.GetItemAsync(queueId);

        // Acceptance asserts this, so a reference that is not a spool reference cannot become mail
        // that vanishes after a restart. Asserting it here pins the boundary.
        Assert.True(PayloadReferences.IsDurable(item!.Envelope.PayloadReference));
        Assert.StartsWith(PayloadReferences.SpoolScheme, item.Envelope.PayloadReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_untrusted_message_id_is_carried_through_as_untrusted()
    {
        using var h = new QueueHarness();

        var submission = QueueHarness.Submission() with { UntrustedMessageIdHeader = "<abc@relay.example>" };
        var queueId = await h.AcceptAsync(submission);

        var item = await h.Store.GetItemAsync(queueId);

        // Recorded for diagnostics and loop tracing. It is not a key, not an identity claim, and
        // dropping it silently would have been its own kind of lie about what we observed.
        Assert.Equal("<abc@relay.example>", item!.Envelope.UntrustedMessageIdHeader);
    }

    [Fact]
    public async Task A_submission_with_no_message_id_reports_none_rather_than_a_placeholder()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        Assert.Null((await h.Store.GetItemAsync(queueId))!.Envelope.UntrustedMessageIdHeader);
    }
}
