using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Auth;
using StyloMail.Host.Contracts;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// <c>GET /v1/senders</c> and <c>GET /v1/messages</c> — the two read listings the operator console
/// is built on.
/// </summary>
/// <remarks>
/// Both are tenant-scoped from the principal and privilege-separated from sending. The tests here are
/// mostly about what a caller <em>cannot</em> see: another tenant's rows, and — for the sender
/// listing, which is built from the configuration that holds them — any credential at all.
/// </remarks>
public sealed class ListingEndpointsTests
{
    // ---------------------------------------------------------------------------------------------
    // GET /v1/senders
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sender_listing_requires_authentication()
    {
        using var host = new TestHost();
        using var client = host.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task A_sending_principal_cannot_enumerate_its_tenants_senders()
    {
        // Review is a separate grant from Send. A sender learning every other principal it shares a
        // tenant with is reconnaissance it has no need for, and the separation is the same one the
        // decision ledger keeps.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeSenderKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task The_sender_listing_names_no_credential()
    {
        // The listing is built from the configuration that holds every principal's API key. This is
        // the test that would catch a serialisation of the options object spliced in later, which is
        // the failure that would be hardest to notice and worst to have.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var body = await (await client.GetAsync("/v1/senders")).Content.ReadAsStringAsync();

        foreach (var key in new[]
                 {
                     TestPrincipals.AcmeAssessKey, TestPrincipals.AcmeSenderKey,
                     TestPrincipals.AcmeReviewerKey, TestPrincipals.AcmeOperatorKey,
                     TestPrincipals.GlobexSenderKey, TestPrincipals.GlobexReviewerKey,
                     TestPrincipals.GlobexOperatorKey,
                 })
        {
            Assert.DoesNotContain(key, body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("key", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_sender_listing_contains_this_tenants_principals_and_no_others()
    {
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var listing = await ListingAsync(client);

        Assert.Equal(TestPrincipals.AcmeTenant, listing.TenantId);
        Assert.NotEmpty(listing.Senders);

        Assert.Contains(listing.Senders, s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        // Every row belongs to the acting tenant, so the listing cannot be a cross-tenant enumeration
        // wearing one tenant's name.
        Assert.DoesNotContain(listing.Senders, s => s.PrincipalId == TestPrincipals.GlobexSenderPrincipal);
        Assert.Equal(
            listing.Senders.Select(s => s.PrincipalId).OrderBy(id => id, StringComparer.Ordinal),
            listing.Senders.Select(s => s.PrincipalId));
    }

    [Fact]
    public async Task A_sender_minted_on_the_host_is_listed_with_the_store_as_its_source()
    {
        // A minted principal is a sender like any other, and the console has to be able to see it or
        // the first run of a fresh deployment has no senders at all.
        using var host = new TestHost();
        host.MintKey("svc-minted-only", TestPrincipals.AcmeTenant, "Assess", "Send");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var sender = (await ListingAsync(reviewer)).Senders
            .Single(s => s.PrincipalId == "svc-minted-only");

        Assert.Equal("store", sender.Source);
    }

    [Fact]
    public async Task A_configured_sender_is_listed_with_the_environment_as_its_source()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var sender = (await ListingAsync(reviewer)).Senders
            .Single(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        // Read-only, and the console needs to know that before it offers a control that cannot work.
        Assert.Equal("environment", sender.Source);
    }

    [Fact]
    public async Task A_sender_that_is_both_configured_and_minted_appears_once_as_a_store_sender()
    {
        // The case that motivated this projection, pinned rather than left to fall out of the fix.
        //
        // Wholesale precedence means the configuration entry for this name authenticates nothing,
        // and the sender listing lists only principals that can authenticate. Excluding the entry
        // without including the store's row for the same name therefore removed the sender from the
        // operator's view entirely: a name that existed, could send, and had simply gone invisible
        // because someone minted a key for it. Silently disappearing is worse than appearing with
        // the wrong provenance, so all three things are asserted here: present, exactly once, and
        // sourced from the store.
        using var host = new TestHost();
        host.MintKey(TestPrincipals.AcmeSenderPrincipal, TestPrincipals.AcmeTenant, "Assess", "Send");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var listing = await ListingAsync(reviewer);

        var matching = listing.Senders
            .Where(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal)
            .ToList();

        var sender = Assert.Single(matching);
        Assert.Equal("store", sender.Source);

        // And the configuration's key really is dead, so "listed" is not standing in for "still
        // works by the old route". Without this the test would pass on a listing that reported the
        // store as the source while the environment entry was quietly still the one resolving.
        using var configuredKey = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await configuredKey.GetAsync("/v1/senders")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_minted_sender_is_not_listed()
    {
        // The same rule the listing already applies to a configuration entry with no key: it cannot
        // authenticate, so listing it would advertise an account that does not exist. Asserted both
        // ways, because "absent" passes against a listing that never had it.
        using var host = new TestHost();
        host.MintKey("svc-minted-only", TestPrincipals.AcmeTenant, "Assess", "Send");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        Assert.Contains(
            (await ListingAsync(reviewer)).Senders,
            s => s.PrincipalId == "svc-minted-only");

        using (var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            var revoke = await operatorClient.PostAsJsonAsync(
                "/v1/controls/senders/svc-minted-only/pause", new { reason = "unused in this test" });

            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        // Pausing is not revoking, so it must not remove the row: a paused sender is still a sender,
        // and a listing that dropped one would make the pause look like a deletion.
        Assert.Contains(
            (await ListingAsync(reviewer)).Senders,
            s => s.PrincipalId == "svc-minted-only");

        host.Services.GetRequiredService<MintedPrincipalStore>()
            .Revoke("svc-minted-only", "test", DateTimeOffset.UtcNow);

        Assert.DoesNotContain(
            (await ListingAsync(reviewer)).Senders,
            s => s.PrincipalId == "svc-minted-only");
    }

    [Fact]
    public async Task The_sender_listing_names_no_minted_credential_either()
    {
        // The configuration is not the only thing that holds a credential any more, and this is the
        // same trap one source over: the projection must not carry the value, and the store's own
        // record has no field for it to carry.
        using var host = new TestHost();
        var minted = host.MintKey("svc-minted-only", TestPrincipals.AcmeTenant, "Assess", "Review");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var body = await (await reviewer.GetAsync("/v1/senders")).Content.ReadAsStringAsync();

        Assert.Contains("svc-minted-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain(minted, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_principal_never_controlled_is_reported_as_unpaused_rather_than_omitted()
    {
        // "Never paused" is the ordinary state, not an absence. A caller that had to tell "no record"
        // from "not paused" would be reading a distinction the control plane does not make.
        using var host = new TestHost();
        using var client = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var sender = (await ListingAsync(client)).Senders
            .Single(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        Assert.False(sender.Control.Paused);
        Assert.Null(sender.Control.PausedAt);
        Assert.Null(sender.Control.Reason);
        Assert.Null(sender.Control.UpdatedBy);
    }

    [Fact]
    public async Task A_paused_principal_carries_the_reason_and_who_set_it()
    {
        // The console's pause control is only half usable without this: a principal id has to be
        // known out of band before pause/resume can be reached at all, and the reason is what
        // distinguishes a containment action from a mistake.
        using var host = new TestHost();

        using (var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            await operatorClient.PostAsJsonAsync(
                $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
                new { reason = "suspected compromise" });
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var sender = (await ListingAsync(reviewer)).Senders
            .Single(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        Assert.True(sender.Control.Paused);
        Assert.Equal("suspected compromise", sender.Control.Reason);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, sender.Control.UpdatedBy);
        Assert.NotNull(sender.Control.PausedAt);
    }

    [Fact]
    public async Task A_resume_lifts_the_pause_without_erasing_it_from_the_listing()
    {
        using var host = new TestHost();

        using (var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            await operatorClient.PostAsJsonAsync(
                $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
                new { reason = "suspected compromise" });

            await operatorClient.PostAsJsonAsync(
                $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/resume",
                new { reason = "false positive" });
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var sender = (await ListingAsync(reviewer)).Senders
            .Single(s => s.PrincipalId == TestPrincipals.AcmeSenderPrincipal);

        // Two-sided, because "nothing happened" claims pass against a no-op: the pause must be
        // lifted *and* the record that one was imposed must survive.
        Assert.False(sender.Control.Paused);
        Assert.Equal("suspected compromise", sender.Control.Reason);
        Assert.NotNull(sender.Control.PausedAt);
        Assert.Equal("false positive", sender.Control.ResumeReason);
        Assert.Equal(TestPrincipals.AcmeOperatorPrincipal, sender.Control.ResumedBy);
    }

    [Fact]
    public async Task A_pause_in_one_tenant_does_not_appear_in_anothers_listing()
    {
        using var host = new TestHost();

        using (var operatorClient = host.ClientAs(TestPrincipals.AcmeOperatorKey))
        {
            await operatorClient.PostAsJsonAsync(
                $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
                new { reason = "suspected compromise" });
        }

        using var globex = host.ClientAs(TestPrincipals.GlobexReviewerKey);

        var listing = await ListingAsync(globex);

        Assert.Equal(TestPrincipals.GlobexTenant, listing.TenantId);
        Assert.All(listing.Senders, s => Assert.False(s.Control.Paused));
    }

    // ---------------------------------------------------------------------------------------------
    // GET /v1/messages
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_message_listing_requires_review()
    {
        using var host = new TestHost();

        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/messages")).StatusCode);

        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/messages")).StatusCode);
    }

    [Fact]
    public async Task The_message_listing_returns_quarantined_mail_with_per_recipient_state()
    {
        using var host = new TestHost();
        await SubmitAsync(host, TestPrincipals.AcmeSenderKey, "msg-quarantine", MailAction.Quarantine);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await MessagesAsync(reviewer, "quarantined");

        var message = Assert.Single(page.Messages);

        Assert.Equal(DeliveryState.Quarantined, message.State);
        Assert.NotEmpty(message.Recipients);
        Assert.All(message.Recipients, r => Assert.Equal(DeliveryState.Quarantined, r.State));
    }

    [Fact]
    public async Task The_message_listing_filters_by_what_is_awaiting_a_decision()
    {
        // The default filter, and the one the console's first screen wants: everything a human has
        // to look at, without the mail that is simply in flight.
        using var host = new TestHost();
        await SubmitAsync(host, TestPrincipals.AcmeSenderKey, "msg-held", MailAction.Hold);
        await SubmitAsync(host, TestPrincipals.AcmeSenderKey, "msg-quarantined", MailAction.Quarantine);
        await SubmitAsync(host, TestPrincipals.AcmeSenderKey, "msg-allowed", MailAction.Allow);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await MessagesAsync(reviewer, state: null);

        Assert.Equal(2, page.Messages.Count);
        Assert.Contains(page.Messages, m => m.State == DeliveryState.Held);
        Assert.Contains(page.Messages, m => m.State == DeliveryState.Quarantined);
        Assert.DoesNotContain(page.Messages, m => m.State == DeliveryState.Queued);
    }

    [Fact]
    public async Task A_state_this_host_does_not_enumerate_is_refused_rather_than_quietly_defaulted()
    {
        // Faking `queued` by filtering the page after it was cut would be worse than refusing it:
        // pages would come back short or empty, hasMore would be wrong, and a console paging through
        // would watch mail disappear. The refusal says which states exist and what it lists instead.
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync("/v1/messages?state=queued");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_state", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_message_listing_never_returns_another_tenants_mail()
    {
        using var host = new TestHost();
        await SubmitAsync(host, TestPrincipals.GlobexSenderKey, "msg-globex", MailAction.Quarantine);

        using var acme = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await MessagesAsync(acme, "quarantined");

        // Absent rather than forbidden, and the route takes no tenant parameter at all, so there is
        // nothing for a caller to name and nothing to refuse.
        Assert.Empty(page.Messages);
        Assert.Equal(TestPrincipals.AcmeTenant, page.TenantId);
    }

    [Fact]
    public async Task The_message_listing_pages_with_the_queues_own_cursor()
    {
        // The regression guard for a real defect that this test caught, and the reason it is written
        // to assert the *union* of pages rather than "no row appears twice".
        //
        // `QueueStore.ListAsync` used to build its next-cursor from two different rows: it tracked
        // `lastCreatedAt` by overwriting it on every row the reader yielded, so after the loop it held
        // the *probe* row's timestamp — the extra row fetched to learn whether a further page exists —
        // while the id paired with it was the last row *kept*. The next page then skipped every row
        // whose timestamp fell between the two, and the probe row survived only if its GUID happened
        // to sort below the kept row's.
        //
        // Two properties of that are worth keeping in mind here. It was **silent** — two of three
        // messages returned, `hasMore: false`, no error — so a duplicate-only assertion would have
        // passed it. And it was **intermittent**, roughly half of runs, depending on a GUID tiebreak,
        // which is what made it look like a flaky test rather than a defect. `queue-` fixed the cursor
        // to take both halves from the same row, and this went from 6 failures in 12 runs to 0 in 15.
        //
        // If this ever goes red again, it is the queue's cursor, not a loose test: do not relax the
        // assertion to make it pass.
        using var host = new TestHost();

        for (var i = 0; i < 3; i++)
        {
            await SubmitAsync(
                host, TestPrincipals.AcmeSenderKey, $"msg-page-{i}", MailAction.Quarantine,
                subject: $"Figures {i}");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var first = await MessagesAsync(reviewer, "quarantined", limit: 2);
        Assert.Equal(2, first.Messages.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var second = await MessagesAsync(reviewer, "quarantined", limit: 2, after: first.NextCursor);

        Assert.False(second.HasMore);

        // No message appears on both pages, and none is skipped between them — which is the property
        // a cursor exists to give and a naive offset would not.
        var ids = first.Messages.Select(m => m.QueueId)
            .Concat(second.Messages.Select(m => m.QueueId))
            .ToList();

        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_message_listing_row_is_the_same_projection_the_single_message_route_serves()
    {
        // So a console renders its list and its detail from one shape rather than reconciling two.
        using var host = new TestHost();
        var queueId = await SubmitAsync(host, TestPrincipals.AcmeSenderKey, "msg-shape", MailAction.Hold);

        // One client for both reads, and the *reviewer* — the console's own principal. That works
        // only because the detail route accepts Review as well as Send; see the test below for why,
        // and this test would fail on a real console flow if that were reverted.
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var fromListing = (await MessagesAsync(reviewer, "held")).Messages.Single();

        var fromDetail = JsonSerializer.Deserialize<SubmissionStatusResponse>(
            await (await reviewer.GetAsync($"/v1/submissions/{queueId}")).Content.ReadAsStringAsync(),
            StyloMail.Host.Serialization.HostJson.Options);

        Assert.NotNull(fromDetail);
        Assert.Equal(fromDetail!.QueueId, fromListing.QueueId);
        Assert.Equal(fromDetail.State, fromListing.State);
        Assert.Equal(fromDetail.Attempts, fromListing.Attempts);
        Assert.Equal(
            fromDetail.Recipients.Select(r => (r.Recipient, r.State)),
            fromListing.Recipients.Select(r => (r.Recipient, r.State)));
    }

    // ---------------------------------------------------------------------------------------------
    // Reading a submission: Send or Review
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_review_only_principal_can_read_a_submission_it_could_already_release()
    {
        // The asymmetry this closes: `GET /v1/submissions/{id}` required Send while
        // `POST /v1/quarantine/{id}/release` required Review, on the same queue id — so a reviewer
        // could release a quarantined message and not inspect it first. Reading is strictly weaker
        // than releasing, so requiring the greater capability for the lesser act was incoherent.
        //
        // Asserted against the *same* id the release works on, because that is the claim: the two
        // routes now agree about who may act on one queue id.
        using var host = new TestHost();
        var queueId = await SubmitAsync(
            host, TestPrincipals.AcmeSenderKey, "msg-reviewer-read", MailAction.Quarantine);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // The reviewer holds Review and Assess, never Send — asserted rather than assumed, because a
        // future change to the test principals that quietly added Send would make this test prove
        // nothing while staying green.
        Assert.False(reviewer.DefaultRequestHeaders.Contains("Idempotency-Key"));

        var read = await reviewer.GetAsync($"/v1/submissions/{queueId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var body = JsonSerializer.Deserialize<SubmissionStatusResponse>(
            await read.Content.ReadAsStringAsync(),
            StyloMail.Host.Serialization.HostJson.Options);

        Assert.NotNull(body);
        Assert.Equal(queueId, body!.QueueId);
        Assert.Equal(DeliveryState.Quarantined, body.State);

        // And the release they were already permitted, on that same id, still succeeds.
        var release = await reviewer.PostAsync($"/v1/quarantine/{queueId}/release", null);
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
    }

    [Fact]
    public async Task Reading_a_submission_still_excludes_whoever_holds_neither_privilege()
    {
        // The union is Send-or-Review, not "anyone authenticated". An assess-only principal reads
        // nothing: neither privilege implies the other, and this is the half of the widening that
        // would be easy to lose.
        using var host = new TestHost();
        var queueId = await SubmitAsync(
            host, TestPrincipals.AcmeSenderKey, "msg-assess-only", MailAction.Quarantine);

        using var assessOnly = host.ClientAs(TestPrincipals.AcmeAssessKey);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await assessOnly.GetAsync($"/v1/submissions/{queueId}")).StatusCode);
    }

    [Fact]
    public async Task Widening_the_read_did_not_widen_everything_else()
    {
        // A union policy is one careless `RequireAuthorization` call away from spreading. The routes
        // either side of it must be unchanged: a reviewer cannot submit mail, and cannot pause a
        // sender.
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var submit = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request()),
        };

        submit.Headers.Add("Idempotency-Key", "key-reviewer-submit");

        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.SendAsync(submit)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await reviewer.PostAsJsonAsync(
                $"/v1/controls/senders/{TestPrincipals.AcmeSenderPrincipal}/pause",
                new { reason = "should not be permitted" })).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task<SenderListingResponse> ListingAsync(HttpClient client)
    {
        var response = await client.GetAsync("/v1/senders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var listing = JsonSerializer.Deserialize<SenderListingResponse>(
            await response.Content.ReadAsStringAsync(),
            StyloMail.Host.Serialization.HostJson.Options);

        return Assert.IsType<SenderListingResponse>(listing);
    }

    private static async Task<MessageListingResponse> MessagesAsync(
        HttpClient client,
        string? state,
        int? limit = null,
        string? after = null)
    {
        var query = $"?state={state ?? string.Empty}"
            + (limit is null ? string.Empty : $"&limit={limit}")
            + (after is null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");

        var response = await client.GetAsync($"/v1/messages{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = JsonSerializer.Deserialize<MessageListingResponse>(
            await response.Content.ReadAsStringAsync(),
            StyloMail.Host.Serialization.HostJson.Options);

        return Assert.IsType<MessageListingResponse>(page);
    }

    private static async Task<string> SubmitAsync(
        TestHost host,
        string apiKey,
        string idempotencyKey,
        MailAction action,
        string subject = "Quarterly figures")
    {
        host.Assessor.Action = action;

        using var client = host.ClientAs(apiKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
        {
            Content = JsonContent.Create(TestMessages.Request(
                rawMime: TestMessages.Base64(TestMessages.SampleMime.Replace("Quarterly figures", subject)))),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("queueId").GetString()!;
    }
}
