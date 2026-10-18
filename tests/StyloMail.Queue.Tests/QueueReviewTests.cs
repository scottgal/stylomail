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
}
