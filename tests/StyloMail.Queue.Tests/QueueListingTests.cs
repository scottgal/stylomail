using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// The listing surface a reviewer uses to find mail awaiting a decision.
/// </summary>
public class QueueListingTests
{
    [Fact]
    public async Task A_listing_is_scoped_to_its_tenant()
    {
        using var h = new QueueHarness();

        var mine = await h.AcceptAsync(QueueHarness.Submission(tenantId: "acme", state: DeliveryState.Held));
        await h.AcceptAsync(QueueHarness.Submission(tenantId: "globex", state: DeliveryState.Held));

        var page = await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" });

        Assert.Equal(mine, Assert.Single(page.Items).QueueId);

        // A tenant with nothing to show gets an empty page, not an error and not another tenant's
        // mail. Callers cannot tell "no such tenant" from "nothing held", so there is no oracle.
        var other = await h.Store.ListAsync(new QueueListingQuery { TenantId = "nobody" });
        Assert.Empty(other.Items);
        Assert.False(other.HasMore);
    }

    [Fact]
    public async Task A_listing_returns_per_recipient_dispositions_without_a_second_lookup()
    {
        using var h = new QueueHarness();

        var queueId = await h.AcceptAsync(QueueHarness.Submission(
            state: DeliveryState.Quarantined,
            recipients: ["a@example.test", "b@example.test"]));

        var item = Assert.Single((await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" })).Items);

        Assert.Equal(queueId, item.QueueId);
        Assert.Equal(2, item.Recipients.Count);
        Assert.All(item.Recipients, r => Assert.Equal(DeliveryState.Quarantined, r.State));
        Assert.Equal(QueueItemOutcome.Pending, item.Outcome);
    }

    [Fact]
    public async Task The_filter_selects_held_quarantined_or_either()
    {
        using var h = new QueueHarness();

        await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));
        await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Quarantined));
        await h.AcceptAsync(QueueHarness.Submission());   // queued: nothing to review

        var either = await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" });
        var held = await h.Store.ListAsync(
            new QueueListingQuery { TenantId = "acme", Filter = QueueListingFilter.Held });
        var quarantined = await h.Store.ListAsync(
            new QueueListingQuery { TenantId = "acme", Filter = QueueListingFilter.Quarantined });

        // A message needing no decision is not a reviewer's business.
        Assert.Equal(2, either.Items.Count);
        Assert.Single(held.Items);
        Assert.Single(quarantined.Items);
        Assert.Equal(DeliveryState.Held, held.Items[0].Recipients.Single().State);
        Assert.Equal(DeliveryState.Quarantined, quarantined.Items[0].Recipients.Single().State);
    }

    [Fact]
    public async Task A_settled_message_drops_out_of_the_listing()
    {
        using var h = new QueueHarness();

        var queueId = await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Quarantined));
        Assert.Single((await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" })).Items);

        await h.Store.ResolveQuarantineAsync(queueId, QuarantineResolution.Reject, "reviewer-1");

        // Decided is decided: a reviewer's list must not keep offering work they have done.
        Assert.Empty((await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" })).Items);
    }

    [Fact]
    public async Task Paging_visits_every_item_exactly_once()
    {
        using var h = new QueueHarness();

        // Deliberately never advancing the clock: every item shares one created_at, so ordering
        // falls entirely to the queue_id tiebreaker. Without a total order a keyset cursor can skip
        // or repeat rows at a page boundary, and this is the case that would expose it.
        const int total = 25;
        var accepted = new HashSet<string>();
        for (var i = 0; i < total; i++)
        {
            accepted.Add(await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held)));
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await h.Store.ListAsync(new QueueListingQuery
            {
                TenantId = "acme",
                Limit = 3,
                After = cursor,
            });

            seen.AddRange(page.Items.Select(i => i.QueueId));
            cursor = page.NextCursor;
            pages++;

            Assert.True(pages < 50, "Paging did not terminate, the cursor is not advancing.");
        }
        while (cursor is not null);

        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());
        Assert.Equal(accepted, seen.ToHashSet());

        // The last page must not claim there is more.
        Assert.Null(cursor);
    }

    /// <remarks>
    /// <b>This exists because the test above could not see the cursor bug, and the reason is subtle.</b>
    /// That test deliberately gives every item the *same* <c>created_at</c> to exercise the
    /// <c>queue_id</c> tiebreaker, and with equal timestamps, the probe row's <c>created_at</c> and
    /// the kept row's are identical, so pairing the probe's timestamp with the kept row's id is
    /// **invisible**. The two halves of the cursor have to differ for the defect to show, which means
    /// the clock must move.
    ///
    /// <para>
    /// Found by <c>ingress-</c>, who hit it wiring the operator console and reported the mechanism
    /// rather than the symptom. Without distinct timestamps the bug skips rows silently on roughly
    /// half of runs: a listing that returns two of three messages while claiming to be complete, and
    /// looks like flakiness from outside.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Paging_visits_every_item_exactly_once_with_distinct_timestamps()
    {
        using var h = new QueueHarness();

        const int total = 25;
        var accepted = new HashSet<string>();

        for (var i = 0; i < total; i++)
        {
            accepted.Add(await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held)));

            // The whole point: every item gets its own created_at.
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await h.Store.ListAsync(new QueueListingQuery
            {
                TenantId = "acme",
                Limit = 3,
                After = cursor,
            });

            seen.AddRange(page.Items.Select(i => i.QueueId));
            cursor = page.NextCursor;
            pages++;

            Assert.True(pages < 50, "Paging did not terminate: the cursor is not advancing.");
        }
        while (cursor is not null);

        // Every message exactly once. The bug this guards dropped whatever fell between the probe's
        // timestamp and the last kept row's.
        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());
        Assert.Equal(accepted, seen.ToHashSet());
    }

    [Fact]
    public async Task A_page_is_clamped_to_the_hard_ceiling_rather_than_rejected()
    {
        using var h = new QueueHarness();

        // More rows than the ceiling. Important: with only a handful of items this claim is
        // untestable, a page smaller than the ceiling proves nothing about clamping, and the
        // test would pass identically if `Limit` were ignored entirely.
        var total = QueueListingLimits.MaxPageSize + 5;
        for (var i = 0; i < total; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));
        }

        var page = await h.Store.ListAsync(new QueueListingQuery
        {
            TenantId = "acme",
            Limit = int.MaxValue,
        });

        // Clamped, not rejected: an oversized request degrades into paging instead of an error,
        // and no caller can pull the whole queue in one go.
        Assert.Equal(QueueListingLimits.MaxPageSize, page.Items.Count);
        Assert.True(page.HasMore, "A full page must advertise that more exists.");
    }

    [Fact]
    public async Task A_tenant_asking_for_more_than_it_has_gets_exactly_what_it_has()
    {
        using var h = new QueueHarness();

        for (var i = 0; i < 3; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));
        }

        var page = await h.Store.ListAsync(new QueueListingQuery { TenantId = "acme" });

        // The other half of the clamping contract: a page is never padded, and an unfilled page
        // must not claim there is more.
        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextCursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task A_malformed_cursor_fails_loudly_instead_of_restarting_the_listing()
    {
        using var h = new QueueHarness();
        await h.AcceptAsync(QueueHarness.Submission(state: DeliveryState.Held));

        // Silently treating a bad cursor as "no cursor" would replay page one forever, which on a
        // review surface looks like the list is broken rather than the client.
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.ListAsync(
            new QueueListingQuery { TenantId = "acme", After = "not-a-cursor" }));

        // Right shape, unparseable timestamp. Must be an argument error, not a leaked
        // FormatException from deep inside date parsing.
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.ListAsync(
            new QueueListingQuery { TenantId = "acme", After = "v1~not-a-date~q-1" }));
    }

    [Fact]
    public async Task A_cursor_cannot_be_used_to_reach_another_tenants_items()
    {
        using var h = new QueueHarness();

        for (var i = 0; i < 4; i++)
        {
            await h.AcceptAsync(QueueHarness.Submission(tenantId: "globex", state: DeliveryState.Held));
        }

        var globexPage = await h.Store.ListAsync(new QueueListingQuery { TenantId = "globex", Limit = 2 });
        Assert.Equal(2, globexPage.Items.Count);
        Assert.NotNull(globexPage.NextCursor);

        // Globex's cursor, replayed against Acme's tenant. The cursor is not a capability: the
        // query's own TenantId governs, so this can only shift which page you see, never whose rows.
        var acme = await h.Store.ListAsync(new QueueListingQuery
        {
            TenantId = "acme",
            After = globexPage.NextCursor,
        });

        Assert.Empty(acme.Items);
        Assert.All(acme.Items, i => Assert.Equal("acme", i.TenantId));
    }
}
