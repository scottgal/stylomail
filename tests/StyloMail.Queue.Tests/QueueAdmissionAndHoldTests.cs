using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// Per-tenant admission control, and the hold window, which is a policy question, not a delivery
/// outcome.
/// </summary>
public class QueueAdmissionAndHoldTests
{
    [Fact]
    public async Task One_tenant_cannot_occupy_the_queue()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxQueuedItemsPerTenant = 2,
        });

        await h.AcceptAsync(QueueHarness.Submission(tenantId: "acme"));
        await h.AcceptAsync(QueueHarness.Submission(tenantId: "acme"));

        var refused = await h.Store.AcceptAsync(QueueHarness.Submission(tenantId: "acme"));

        Assert.Equal(QueueAdmission.RefusedTenantItemLimit, refused.Admission);
        Assert.False(refused.IsAccepted);

        // A different tenant is entirely unaffected. That is the whole point of bounding per tenant
        // rather than bounding the queue as a whole.
        var other = await h.Store.AcceptAsync(QueueHarness.Submission(tenantId: "globex"));
        Assert.True(other.IsAccepted);

        Assert.Equal(3, Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task One_tenant_cannot_exhaust_the_spool()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxPayloadBytes = 1024,
            MaxLivePayloadBytesPerTenant = 2048,
        });

        await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));
        await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));

        var refused = await h.Store.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));

        Assert.Equal(QueueAdmission.RefusedTenantByteLimit, refused.Admission);
        Assert.False(refused.IsAccepted);

        // Bytes, not just rows: the refusal left nothing on disk.
        Assert.Equal(2, Directory.GetFiles(h.SpoolRoot, "*.eml", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task A_delivered_but_unpurged_payload_still_occupies_the_spool_budget()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxPayloadBytes = 1024,
            MaxLivePayloadBytesPerTenant = 2048,
            TerminalPayloadRetention = TimeSpan.FromDays(30),   // nothing purges during this test
        });

        await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));
        await h.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));

        // Deliver one. It is terminal, so it no longer occupies the *queue*...
        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        var state = (await h.Store.CountByStateAsync("acme"));
        Assert.Equal(1, state[DeliveryState.Delivered]);

        // ...but its bytes are still on the volume, so they still count against the spool.
        var refused = await h.Store.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024));

        // This is the claim that separates the byte budget from the item-count budget: "it counts
        // every payload still on disk, including those of terminal items awaiting purge". If
        // delivered payloads were excluded, live bytes would be 1024 and this would be accepted.
        Assert.Equal(QueueAdmission.RefusedTenantByteLimit, refused.Admission);
    }

    [Fact]
    public async Task Settling_a_message_frees_its_place_in_the_queue()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            MaxQueuedItemsPerTenant = 1,
        });

        await h.AcceptAsync(QueueHarness.Submission());
        Assert.Equal(
            QueueAdmission.RefusedTenantItemLimit,
            (await h.Store.AcceptAsync(QueueHarness.Submission())).Admission);

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.Delivered("worker-a", "rcpt@example.test"));

        // The bound counts undelivered work, so delivering releases the slot.
        Assert.True((await h.Store.AcceptAsync(QueueHarness.Submission())).IsAccepted);
    }

    [Fact]
    public async Task A_held_recipient_does_not_consume_the_delivery_queue()
    {
        using var h = new QueueHarness();

        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Held,
            reEvaluateBy: QueueHarness.Start.AddHours(1)));

        // A hold is durably retained, not deliverable.
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Held, item!.State);
        Assert.True(h.Spool.Exists(item.Envelope.PayloadReference));
        Assert.Equal(DeliveryState.Held, (await h.Store.CountByStateAsync()).Single().Key);
    }

    [Fact]
    public async Task A_hold_with_no_stated_deadline_gets_the_configured_bound_rather_than_throwing()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            DefaultHoldWindow = TimeSpan.FromHours(6),
        });

        // A caller that omits the deadline. This is the case a consumer believed threw, a doc
        // comment claimed the field was "required when Held". It never was, and the behaviour is
        // better than an exception: a hold is defined as a bounded window, so the system cannot
        // represent the indefinite retention the spec forbids, and a missing deadline resolves to
        // the configured bound instead of refusing a message the MTA already handed us.
        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));

        var recipient = QueueHarness.By((await h.Store.GetItemAsync(queueId))!, "rcpt@example.test");
        Assert.Equal(DeliveryState.Held, recipient.State);
        Assert.Equal(QueueHarness.Start.AddHours(6), recipient.ReEvaluateBy);

        // And it is genuinely bounded: the hold surfaces on schedule.
        h.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        Assert.Contains(queueId, (await h.Store.RecoverAsync()).ExpiredHolds);
    }

    [Fact]
    public async Task An_expired_hold_is_surfaced_as_a_policy_decision_and_not_an_acknowledgement()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            DefaultHoldWindow = TimeSpan.FromHours(4),
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));

        // Held: nothing to deliver, and nothing to fail.
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        h.Clock.AdvanceMinutes(5 * 60);
        var recovery = await h.Store.RecoverAsync();

        Assert.Contains(queueId, recovery.ExpiredHolds);

        var item = await h.Store.GetItemAsync(queueId);
        var recipient = QueueHarness.By(item!, "rcpt@example.test");

        // The queue did not decide anything. The recipient is still held, not delivered, nothing
        // was sent, and an elapsed timer is not evidence that anything was.
        Assert.Equal(DeliveryState.Held, recipient.State);
        Assert.Null(recipient.DeliveredAt);

        var attempt = Assert.Single(await h.Store.GetAttemptsAsync(queueId));
        Assert.Equal(DeliveryAttemptOutcome.HoldExpired, attempt.Outcome);

        // Surfaced once per lapsed hold, not once per sweep.
        Assert.Empty((await h.Store.RecoverAsync()).ExpiredHolds);
    }

    [Fact]
    public async Task Releasing_a_hold_makes_the_message_deliverable_at_policys_direction()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, DefaultHoldWindow = TimeSpan.FromHours(1) });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));

        h.Clock.AdvanceMinutes(61);
        await h.Store.RecoverAsync();

        Assert.True(await h.Store.ResolveHoldAsync(queueId, HoldResolution.Deliver, "reviewer-1"));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        // Held messages were never attempted, so they keep their full delivery budget.
        Assert.Equal(0, QueueHarness.By(lease!.Item, "rcpt@example.test").Attempts);
        Assert.Single(lease.PendingRecipients);

        // The decision itself is in the audit trail, naming who made it.
        var decision = Assert.Single(
            await h.Store.GetAttemptsAsync(queueId),
            a => a.Outcome == DeliveryAttemptOutcome.HoldResolved);
        Assert.Equal("reviewer-1", decision.WorkerId);
    }

    [Fact]
    public async Task Quarantining_a_hold_keeps_the_payload_and_stops_delivery()
    {
        using var h = new QueueHarness(c => new QueueOptions { TimeProvider = c, DefaultHoldWindow = TimeSpan.FromHours(1) });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));

        h.Clock.AdvanceMinutes(61);
        await h.Store.RecoverAsync();

        Assert.True(await h.Store.ResolveHoldAsync(queueId, HoldResolution.Quarantine, "policy-1"));

        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Quarantined, QueueHarness.By(item!, "rcpt@example.test").State);

        // Quarantine retains the message; only review may release or destroy it.
        Assert.True(h.Spool.Exists(item!.Envelope.PayloadReference));

        // And resolving a message with no held recipients is a no-op, not an error.
        Assert.False(await h.Store.ResolveHoldAsync(queueId, HoldResolution.Deliver, "reviewer-1"));
    }

    [Fact]
    public async Task A_quarantined_message_is_retained_indefinitely_but_still_counts_against_the_spool()
    {
        using var h = new QueueHarness(c => new QueueOptions
        {
            TimeProvider = c,
            DefaultHoldWindow = TimeSpan.FromHours(1),
            TerminalPayloadRetention = TimeSpan.FromMinutes(1),
            MaxPayloadBytes = 1024,
            MaxLivePayloadBytesPerTenant = 1024,
        });

        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            payloadBytes: 1024, state: DeliveryState.Held));

        h.Clock.AdvanceMinutes(61);
        await h.Store.RecoverAsync();
        await h.Store.ResolveHoldAsync(queueId, HoldResolution.Quarantine, "policy-1");

        // Long past any retention window, and still ours: quarantine is not a terminal state.
        h.Clock.Advance(TimeSpan.FromDays(30));
        var recovery = await h.Store.RecoverAsync();

        Assert.Empty(recovery.PurgedPayloads);
        Assert.True(h.Spool.Exists((await h.Store.GetItemAsync(queueId))!.Envelope.PayloadReference));

        // Which means it still occupies the tenant's spool budget, quarantine has to be bounded
        // somewhere, and admission control is that bound.
        Assert.Equal(
            QueueAdmission.RefusedTenantByteLimit,
            (await h.Store.AcceptAsync(QueueHarness.Submission(payloadBytes: 1024))).Admission);
    }
}
