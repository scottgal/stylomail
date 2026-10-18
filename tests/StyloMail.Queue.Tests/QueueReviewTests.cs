using System.Text;
using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The review state: withheld for a human, never claimed, escalates only to quarantine.
/// </summary>
public sealed class QueueReviewTests
{
    [Fact]
    public async Task A_review_is_never_claimed_for_delivery()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal(DeliveryState.Review, item.State);
        var recipient = QueueHarness.By(item, "rcpt@example.test");
        Assert.Equal(DeliveryState.Review, recipient.State);
        Assert.Equal(QueueHarness.Start.AddHours(1), recipient.ReviewDeadline);
        Assert.False(recipient.IsWorkable);
    }

    [Fact]
    public async Task A_review_admission_without_a_deadline_is_refused()
    {
        using var h = new QueueHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(state: DeliveryState.Review)));
    }

    [Fact]
    public async Task A_review_admission_without_an_opening_is_refused()
    {
        using var h = new QueueHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(1),
                includeReviewOpening: false)));
    }

    [Fact]
    public async Task Releasing_a_review_returns_it_to_delivery()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.True(await h.Store.ResolveReviewAsync(
            queueId, ReviewResolution.Release, "reviewer-7", "recipient confirmed it",
            assessmentId: "assessment-1", policyReference: "pack@1/rules@1"));

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal(DeliveryState.Queued, item.State);
        Assert.Null(QueueHarness.By(item, "rcpt@example.test").ReviewDeadline);

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);
        Assert.Equal(queueId, lease!.QueueId);

        var opening = await h.Store.GetReviewOpeningAsync("acme", queueId);
        Assert.NotNull(opening);
        Assert.Equal("asm-review-1", opening!.Opening.AssessmentId);

        var events = await h.Store.ListReviewEventsAsync("acme", queueId);
        var opened = Assert.Single(events, @event => @event.Event == "review.opened");
        Assert.Equal(opening.OpeningEventId, opened.EventId);
        Assert.Equal("asm-review-1", opened.AssessmentId);
        Assert.Contains("rule-payment-redirection@1", opened.PolicyReference!);

        var released = Assert.Single(events, @event => @event.Event == "review.released");
        Assert.Equal(opening.OpeningEventId, released.OpeningEventId);
        Assert.Equal("asm-review-1", released.AssessmentId);
        Assert.Equal("reviewer-7", released.Actor);
        Assert.Equal("asm-review-1", released.AssessmentId);
        Assert.Contains("rule-payment-redirection@1", released.PolicyReference!);
        Assert.Equal(DeliveryState.Review, released.FromState);
        Assert.Equal(DeliveryState.Queued, released.ToState);
    }

    [Fact]
    public async Task A_review_can_be_contained_or_permanently_refused()
    {
        using var h = new QueueHarness();
        var quarantinedId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));
        var rejectedId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.True(await h.Store.ResolveReviewAsync(
            quarantinedId, ReviewResolution.Quarantine, "reviewer-7", "suspicious"));
        Assert.True(await h.Store.ResolveReviewAsync(
            rejectedId, ReviewResolution.Reject, "admin-1", "confirmed phishing"));

        var quarantined = await h.Store.GetItemAsync(quarantinedId);
        var rejected = await h.Store.GetItemAsync(rejectedId);

        Assert.Equal(DeliveryState.Quarantined, quarantined!.State);
        Assert.Equal(DeliveryState.TerminalFailure, rejected!.State);

        // Neither is deliverable, and quarantine is resolved through the existing surface.
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));
        Assert.True(await h.Store.ResolveQuarantineAsync(
            quarantinedId, QuarantineResolution.Release, "reviewer-8"));
    }

    [Fact]
    public async Task The_deadline_sweep_escalates_to_quarantine_and_never_releases()
    {
        using var h = new QueueHarness();
        var deadline = QueueHarness.Start.AddHours(1);
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: deadline));

        h.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, await h.Store.SweepReviewDeadlinesAsync());

        var item = await h.Store.GetItemAsync(queueId);
        Assert.Equal(DeliveryState.Quarantined, item!.State);
        Assert.Equal(DeliveryState.Quarantined, QueueHarness.By(item, "rcpt@example.test").State);
        Assert.Null(await h.Store.ClaimNextAsync("worker-a"));

        var events = await h.Store.ListReviewEventsAsync("acme", queueId);
        var expired = Assert.Single(events, @event => @event.Event == "review.expired");
        Assert.Equal("policy-scheduler", expired.Actor);
        Assert.Equal(DeliveryState.Quarantined, expired.ToState);
        Assert.Equal("asm-review-1", expired.AssessmentId);
        Assert.NotNull(expired.OpeningEventId);
        Assert.Contains("rule-payment-redirection@1", expired.PolicyReference!);

        // Idempotent: a second sweep has nothing to move.
        Assert.Equal(0, await h.Store.SweepReviewDeadlinesAsync());
    }

    [Fact]
    public async Task A_reviewer_who_acts_before_the_sweep_wins_exactly_once()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.True(await h.Store.ResolveReviewAsync(
            queueId, ReviewResolution.Release, "reviewer-7", "cleared"));

        h.Clock.Advance(TimeSpan.FromHours(2));

        // The sweep finds no Review recipient, so it does not quarantine a released message.
        Assert.Equal(0, await h.Store.SweepReviewDeadlinesAsync());
        Assert.Equal(DeliveryState.Queued, (await h.Store.GetItemAsync(queueId))!.State);
    }

    [Fact]
    public async Task The_review_listing_filter_returns_only_reviews()
    {
        using var h = new QueueHarness();
        await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));
        await h.AcceptAsync(QueueHarness.Submission());

        var page = await h.Store.ListAsync(new QueueListingQuery
        {
            TenantId = "acme",
            Filter = QueueListingFilter.Review,
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(DeliveryState.Review, item.State);
    }

    [Fact]
    public async Task A_review_is_tenant_scoped()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.False(await h.Store.ResolveReviewAsync(
            queueId, ReviewResolution.Release, "reviewer-7", "wrong tenant", tenantId: "globex"));

        Assert.Empty(await h.Store.ListReviewEventsAsync("globex", queueId));
        Assert.Equal(DeliveryState.Review, (await h.Store.GetItemAsync(queueId, "acme"))!.State);
    }

    [Theory]
    [InlineData(ReviewResolution.Release)]
    [InlineData(ReviewResolution.Quarantine)]
    [InlineData(ReviewResolution.Reject)]
    public async Task A_review_resolution_does_not_steal_a_live_sibling_lease(ReviewResolution resolution)
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(Mixed(QueueHarness.Start.AddHours(1)));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        Assert.True(await h.Store.ResolveReviewAsync(
            queueId, resolution, "reviewer-7", "sibling delivery is in flight"));

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal("worker-a", item.LeaseOwner);
        Assert.True(await h.Store.RenewLeaseAsync(queueId, "worker-a"));
        Assert.Null(await h.Store.ClaimNextAsync("worker-b"));
    }

    [Fact]
    public async Task The_sweep_does_not_steal_a_live_sibling_lease()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(Mixed(QueueHarness.Start.AddMinutes(1)));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        // Two minutes into a five-minute lease: the review deadline is due, the delivery lease is not.
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await h.Store.SweepReviewDeadlinesAsync());

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal("worker-a", item.LeaseOwner);
        Assert.True(await h.Store.RenewLeaseAsync(queueId, "worker-a"));
        Assert.Null(await h.Store.ClaimNextAsync("worker-b"));
    }

    [Fact]
    public async Task A_fallback_opening_has_null_pack_provenance()
    {
        using var h = new QueueHarness();
        var fallback = new ReviewOpening
        {
            AssessmentId = "asm-fallback",
            Source = ReviewOpeningSource.Fallback,
            ReasonCode = "policy.pack_store_unavailable",
            Reason = "no pack could be read",
            UnavailableReason = "policy.pack_store_unavailable",
            FallbackDeadline = QueueHarness.Start.AddHours(24),
            DecidedAt = QueueHarness.Start,
        };

        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(24),
            reviewOpening: fallback));

        var opening = await h.Store.GetReviewOpeningAsync("acme", queueId);
        Assert.NotNull(opening);
        Assert.Equal(ReviewOpeningSource.Fallback, opening!.Opening.Source);
        Assert.Null(opening.Opening.PackId);
        Assert.Null(opening.Opening.PackVersion);
        Assert.Null(opening.Opening.RuleSetId);
        Assert.Null(opening.Opening.RuleSetVersion);
        Assert.Null(opening.Opening.EvidenceSnapshotDigest);
        Assert.Null(opening.Opening.PolicyReference());
    }

    [Fact]
    public async Task A_pack_opening_without_a_digest_is_refused()
    {
        using var h = new QueueHarness();
        var opening = QueueHarness.DefaultReviewOpening() with
        {
            EvidenceSnapshotDigest = null,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(1),
                reviewOpening: opening)));
    }

    [Fact]
    public async Task A_pack_opening_carrying_an_unavailable_reason_is_refused()
    {
        using var h = new QueueHarness();
        var opening = QueueHarness.DefaultReviewOpening() with
        {
            UnavailableReason = "policy.pack_store_unavailable",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(1),
                reviewOpening: opening)));
    }

    [Fact]
    public async Task A_pack_opening_carrying_a_fallback_deadline_is_refused()
    {
        using var h = new QueueHarness();
        var opening = QueueHarness.DefaultReviewOpening() with
        {
            FallbackDeadline = QueueHarness.Start.AddHours(24),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(1),
                reviewOpening: opening)));
    }

    [Fact]
    public async Task A_fallback_opening_carrying_pack_provenance_is_refused()
    {
        using var h = new QueueHarness();
        var opening = new ReviewOpening
        {
            AssessmentId = "asm-fallback",
            Source = ReviewOpeningSource.Fallback,
            PackId = "pack-commerce",
            PackVersion = "1.0.0",
            RuleSetId = "rules-commerce",
            RuleSetVersion = "rules/1",
            RuleId = "rule-payment",
            RuleVersion = 1,
            ReasonCode = "policy.pack_store_unavailable",
            Reason = "no pack",
            UnavailableReason = "policy.pack_store_unavailable",
            FallbackDeadline = QueueHarness.Start.AddHours(24),
            DecidedAt = QueueHarness.Start,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(24),
                reviewOpening: opening)));
    }

    [Fact]
    public async Task A_pack_opening_without_a_deciding_rule_is_refused()
    {
        using var h = new QueueHarness();
        var opening = QueueHarness.DefaultReviewOpening() with
        {
            RuleId = null,
            RuleVersion = null,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(1),
                reviewOpening: opening)));
    }

    [Fact]
    public async Task A_fallback_opening_without_a_reason_or_deadline_is_refused()
    {
        using var h = new QueueHarness();
        var opening = new ReviewOpening
        {
            AssessmentId = "asm-fallback",
            Source = ReviewOpeningSource.Fallback,
            ReasonCode = "policy.pack_store_unavailable",
            Reason = "no pack",
            DecidedAt = QueueHarness.Start,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.AcceptAsync(
            QueueHarness.Submission(
                state: DeliveryState.Review,
                reviewDeadline: QueueHarness.Start.AddHours(24),
                reviewOpening: opening)));
    }

    [Theory]
    [InlineData(ReviewResolution.Release)]
    [InlineData(ReviewResolution.Quarantine)]
    [InlineData(ReviewResolution.Reject)]
    public async Task An_expired_but_unreclaimed_lease_survives_a_review_resolution(
        ReviewResolution resolution)
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(Mixed(QueueHarness.Start.AddHours(1)));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        // Past the timestamp, but no recovery has run. Ownership has not moved.
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(await h.Store.ResolveReviewAsync(
            queueId, resolution, "reviewer-7", "sibling delivery is still owned"));

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal(DeliveryState.Delivering, item.State);
        Assert.Equal("worker-a", item.LeaseOwner);
        Assert.True(await h.Store.RenewLeaseAsync(queueId, "worker-a"));
        Assert.Null(await h.Store.ClaimNextAsync("worker-b"));

        var completion = await h.Store.CompleteAsync(
            lease!, QueueHarness.Delivered("worker-a", "queued@example.test"));
        Assert.Equal(QueueCompletionStatus.Applied, completion.Status);
    }

    [Fact]
    public async Task An_expired_but_unreclaimed_lease_survives_the_sweep()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(Mixed(QueueHarness.Start.AddMinutes(1)));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        h.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await h.Store.SweepReviewDeadlinesAsync());

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Equal(DeliveryState.Delivering, item.State);
        Assert.Equal("worker-a", item.LeaseOwner);
        Assert.True(await h.Store.RenewLeaseAsync(queueId, "worker-a"));
        Assert.Null(await h.Store.ClaimNextAsync("worker-b"));

        var completion = await h.Store.CompleteAsync(
            lease!, QueueHarness.Delivered("worker-a", "queued@example.test"));
        Assert.Equal(QueueCompletionStatus.Applied, completion.Status);
    }

    [Fact]
    public async Task A_human_resolution_does_not_consume_a_transport_retry()
    {
        using var h = new QueueHarness(clock => new QueueOptions
        {
            TimeProvider = clock,
            MaxAttemptsPerRecipient = 2,
        });
        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Review,
            reviewDeadline: QueueHarness.Start.AddHours(1)));

        Assert.True(await h.Store.ResolveReviewAsync(
            queueId, ReviewResolution.Release, "reviewer-7", "cleared"));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(lease);

        await h.Store.CompleteAsync(
            lease!, QueueHarness.TemporaryFailure("worker-a", "rcpt@example.test"));

        var item = (await h.Store.GetItemAsync(queueId))!;
        var recipient = QueueHarness.By(item, "rcpt@example.test");
        Assert.Equal(DeliveryState.RetryScheduled, recipient.State);
        Assert.Equal(1, recipient.Attempts);
    }

    [Fact]
    public async Task Actual_recovery_reclaims_an_orphaned_lease_and_delivery_proceeds()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(Mixed(QueueHarness.Start.AddHours(1)));

        var first = await h.Store.ClaimNextAsync("worker-a");
        Assert.NotNull(first);

        // The lease has lapsed and no one has renewed it: recovery is the path that may take it.
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var recovered = await h.Store.RecoverAsync();

        Assert.Contains(queueId, recovered.ReclaimedLeases);

        var item = (await h.Store.GetItemAsync(queueId))!;
        Assert.Null(item.LeaseOwner);

        var second = await h.Store.ClaimNextAsync("worker-b");
        Assert.NotNull(second);
        Assert.Equal(queueId, second!.QueueId);

        var completion = await h.Store.CompleteAsync(
            second, QueueHarness.Delivered("worker-b", "queued@example.test"));
        Assert.Equal(QueueCompletionStatus.Applied, completion.Status);
    }

    private static QueueSubmission Mixed(DateTimeOffset reviewDeadline) => new()
    {
        TenantId = "acme",
        InternalMessageId = "msg-" + Guid.NewGuid().ToString("N"),
        Direction = MailDirection.Inbound,
        TrustedPrincipalId = "principal-1",
        MailFrom = "sender@acme.test",
        MimeDigest = "digest-" + Guid.NewGuid().ToString("N"),
        Payload = Encoding.UTF8.GetBytes("Subject: mixed\r\n\r\nbody"),
        Recipients =
        [
            new RecipientAdmission
            {
                Recipient = "review@example.test",
                State = DeliveryState.Review,
                ReviewDeadline = reviewDeadline,
            },
            new RecipientAdmission
            {
                Recipient = "queued@example.test",
                State = DeliveryState.Queued,
            },
        ],
        HopCount = 0,
        ReviewOpening = QueueHarness.DefaultReviewOpening(),
    };
}
