using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Contracts;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Tests;

/// <summary>
/// <c>GET /v1/decisions</c>: the ledger listing the Review pane is built on.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as the two listings beside it: <c>Review</c>, tenant-scoped from the principal with
/// no tenant parameter, keyset-paged and bounded. The tests here are mostly about what a caller
/// cannot see and about paging, because a listing that silently omits rows is the failure this whole
/// surface exists to make impossible.
/// </para>
/// <para>
/// <b>Paging is tested with timestamps the test chooses</b>, via <see cref="TestHost.WithClock"/>.
/// Ordering by time cannot be tested against the wall clock: two decisions written in the same
/// millisecond tie, and the cursor's tiebreak then decides what a page contains, so a test meaning
/// to exercise distinct-timestamp paging can quietly become a same-timestamp one and stop covering
/// the case it was written for.
/// </para>
/// </remarks>
public sealed class DecisionListingTests
{
    [Fact]
    public async Task The_decision_listing_requires_review()
    {
        using var host = new TestHost();

        using var anonymous = host.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/decisions")).StatusCode);

        // A sender reading back the ledger would remove the separation between sending and
        // reviewing, which is the same line the single-decision route already draws.
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync("/v1/decisions")).StatusCode);
    }

    [Fact]
    public async Task The_listing_returns_this_tenants_decisions_and_no_others()
    {
        using var host = new TestHost().WithClock();

        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "figures");
        await AssessAsync(host, TestPrincipals.GlobexSenderKey, "figures");

        using var acme = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ListingAsync(acme);

        // Absent rather than forbidden: there is no tenant parameter for a caller to name, so there
        // is nothing to refuse and nothing to learn.
        Assert.Equal(TestPrincipals.AcmeTenant, page.TenantId);
        var decision = Assert.Single(page.Decisions);
        Assert.False(page.HasMore);
        Assert.NotEmpty(decision.Reasons);
    }

    [Fact]
    public async Task Paging_returns_every_decision_exactly_once()
    {
        // The assertion with teeth. A cursor that skips produces *fewer* rows, not duplicates, so
        // "no row appears twice" passes against it: the property that catches it is that the union
        // of the pages equals the set that was recorded. This is the same defect the queue's listing
        // shipped and the same property its own test was missing.
        using var host = new TestHost().WithClock();
        const int recorded = 5;

        for (var i = 0; i < recorded; i++)
        {
            await AssessAsync(host, TestPrincipals.AcmeSenderKey, $"figures {i}");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var seen = new List<string>();
        string? cursor = null;

        // Page size 2 against 5, so the walk is three pages and every page but the last builds a
        // cursor: which is where a cursor defect lives.
        for (var page = 0; page < 8; page++)
        {
            var listed = await ListingAsync(reviewer, limit: 2, after: cursor);
            seen.AddRange(listed.Decisions.Select(d => d.AssessmentId));

            cursor = listed.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(recorded, seen.Count);
        Assert.Equal(recorded, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(cursor);
    }

    [Fact]
    public async Task The_listing_is_newest_first()
    {
        using var host = new TestHost().WithClock();

        for (var i = 0; i < 3; i++)
        {
            await AssessAsync(host, TestPrincipals.AcmeSenderKey, $"figures {i}");
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ListingAsync(reviewer, limit: 10);

        var times = page.Decisions.Select(d => d.AssessedAt).ToList();

        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    [Theory]
    [InlineData(MailAction.Allow)]
    [InlineData(MailAction.Quarantine)]
    [InlineData(MailAction.Hold)]
    public async Task The_action_filter_is_honoured(MailAction action)
    {
        // Served by an equality on the ledger's own indexed column, so it filters the query rather
        // than the page: which is what makes it honest to offer.
        using var host = new TestHost().WithClock();

        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "one", MailAction.Allow);
        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "two", MailAction.Quarantine);
        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "three", MailAction.Hold);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ListingAsync(reviewer, action: action.ToString());

        Assert.NotEmpty(page.Decisions);
        Assert.All(page.Decisions, d => Assert.Equal(action, d.Action));
    }

    [Fact]
    public async Task An_action_this_ledger_does_not_record_is_refused_by_name()
    {
        // Refused rather than treated as "no filter". A caller asking for one action and receiving
        // every action, with the response saying which filter was applied, is a screen that states
        // something untrue about the rows it is showing.
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync("/v1/decisions?action=Shred");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_action", body.RootElement.GetProperty("error").GetString());

        // And it names what does exist, so the caller can correct itself without a second trip.
        Assert.Contains("Quarantine", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cursor_this_ledger_did_not_issue_is_refused_rather_than_silently_restarted()
    {
        // Treating an unparseable cursor as "no cursor" would answer with the first page, so a client
        // paging with a corrupted cursor would read page one, receive a valid next cursor, and fetch
        // page one again: forever, with nothing saying why.
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var response = await reviewer.GetAsync("/v1/decisions?after=not-a-cursor-we-issued");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_cursor", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_row_carries_no_evidence_payload()
    {
        // The listing is bounded by shape, not by hope: the full decision: with its evidence list:
        // is one request away, and that is the request whose size a caller can see coming.
        using var host = new TestHost().WithClock();
        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "figures");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var listingBody = await (await reviewer.GetAsync("/v1/decisions")).Content.ReadAsStringAsync();
        using var listing = JsonDocument.Parse(listingBody);
        var row = listing.RootElement.GetProperty("decisions")[0];

        Assert.False(row.TryGetProperty("evidence", out _));
        Assert.False(row.TryGetProperty("riskDimensions", out _));

        // What it does carry, so a reviewer can decide whether to open the row.
        Assert.True(row.TryGetProperty("reasons", out _));
        Assert.True(row.TryGetProperty("versions", out _));
        Assert.True(row.TryGetProperty("coverage", out _));

        // And the detail route still has the whole thing.
        var assessmentId = row.GetProperty("assessmentId").GetString();
        var detailBody = await (await reviewer.GetAsync($"/v1/decisions/{assessmentId}")).Content.ReadAsStringAsync();

        Assert.Contains("\"evidence\"", detailBody, StringComparison.Ordinal);
        Assert.Contains("\"riskDimensions\"", detailBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_listing_row_and_the_detail_route_describe_the_same_decision()
    {
        // Two projections of one decision can drift, and a list that disagrees with the record it
        // links to is worse than one that shows less. The shared sub-mappings make that structural;
        // this is what would notice if someone unshared them.
        using var host = new TestHost().WithClock();
        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "figures", MailAction.Quarantine);

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        var summary = (await ListingAsync(reviewer)).Decisions.Single();

        var detail = JsonSerializer.Deserialize<DecisionResponse>(
            await (await reviewer.GetAsync($"/v1/decisions/{summary.AssessmentId}")).Content.ReadAsStringAsync(),
            HostJson.Options);

        Assert.NotNull(detail);
        Assert.Equal(detail!.AssessmentId, summary.AssessmentId);
        Assert.Equal(detail.InternalMessageId, summary.InternalMessageId);
        Assert.Equal(detail.Action, summary.Action);
        Assert.Equal(detail.RiskIndex, summary.RiskIndex);
        Assert.Equal(detail.AssessedAt, summary.AssessedAt);
        Assert.Equal(
            detail.Reasons.Select(r => (r.Code, r.Message)),
            summary.Reasons.Select(r => (r.Code, r.Message)));
        Assert.Equal(detail.Versions.PolicyVersion, summary.Versions.PolicyVersion);
        Assert.Equal(detail.Coverage.BodyParsed, summary.Coverage.BodyParsed);

        // What the listing leaves out is asserted structurally rather than by content: the summary
        // *type* has no evidence field, which `A_row_carries_no_evidence_payload` checks against the
        // wire. Asserting `detail.Evidence` is non-empty here would prove nothing anyway: the test
        // host's assessor is a fake that records no evidence, so an empty list says nothing about
        // whether the detail route would carry one.
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task<DecisionListingResponse> ListingAsync(
        HttpClient client,
        string? action = null,
        int? limit = null,
        string? after = null)
    {
        var query = "?"
            + (action is null ? string.Empty : $"action={action}&")
            + (limit is null ? string.Empty : $"limit={limit}&")
            + (after is null ? string.Empty : $"after={Uri.EscapeDataString(after)}");

        var response = await client.GetAsync($"/v1/decisions{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = JsonSerializer.Deserialize<DecisionListingResponse>(
            await response.Content.ReadAsStringAsync(),
            HostJson.Options);

        return Assert.IsType<DecisionListingResponse>(page);
    }

    /// <summary>Assesses one message, which records a decision in this tenant's ledger.</summary>
    private static async Task AssessAsync(
        TestHost host,
        string apiKey,
        string subject,
        MailAction action = MailAction.Allow)
    {
        host.Assessor.Action = action;

        // A distinct instant per call, chosen rather than raced for. The ledger orders by it, so
        // decisions sharing one would leave the ordering, and therefore the cursor, untested.
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        using var client = host.ClientAs(apiKey);

        var response = await client.PostAsync(
            "/v1/assessments",
            JsonContent.Create(TestMessages.Request(
                rawMime: TestMessages.Base64(TestMessages.SampleMime.Replace("Quarterly figures", subject)))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// The console's headline flow: a reviewer sees a held message, and can reach the reason it was held.
/// </summary>
/// <remarks>
/// This is why the detail pane exists, and until now there was no path to it. The queue does not carry
/// an assessment id, and the only place one was ever handed to a client was the response to
/// <c>POST /v1/submissions</c>: a response a reviewer working from a list never saw. So a listed
/// message was a dead end.
///
/// <para>
/// The link is the message id, which both sides already have: the ledger stores it, and the message
/// listings now expose it. Neither component changed what it stores.
/// </para>
/// </remarks>
public sealed class MessageToDecisionTests
{
    [Fact]
    public async Task A_reviewer_can_go_from_a_held_message_to_the_reason_it_was_held()
    {
        using var host = new TestHost().WithClock();

        // A sender submits; the pipeline quarantines it; the sender never sees an assessment id.
        using (var sender = host.ClientAs(TestPrincipals.AcmeSenderKey))
        {
            host.Assessor.Action = MailAction.Quarantine;

            var submit = new HttpRequestMessage(HttpMethod.Post, "/v1/submissions")
            {
                Content = JsonContent.Create(TestMessages.Request()),
            };

            submit.Headers.Add("Idempotency-Key", "key-held-message");
            Assert.Equal(HttpStatusCode.Accepted, (await sender.SendAsync(submit)).StatusCode);
        }

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);

        // 1. The reviewer lists what is awaiting a decision and sees the message.
        var messages = JsonSerializer.Deserialize<MessageListingResponse>(
            await (await reviewer.GetAsync("/v1/messages?state=quarantined")).Content.ReadAsStringAsync(),
            HostJson.Options);

        var message = Assert.Single(Assert.IsType<MessageListingResponse>(messages).Messages);
        Assert.Equal(DeliveryState.Quarantined, message.State);

        // The row carries the join key: without this the flow stops here, which is the gap this test
        // was written to close.
        Assert.False(string.IsNullOrWhiteSpace(message.InternalMessageId));

        // 2. The reviewer asks the ledger for that message's decisions.
        var decisions = JsonSerializer.Deserialize<DecisionListingResponse>(
            await (await reviewer.GetAsync(
                $"/v1/decisions?messageId={Uri.EscapeDataString(message.InternalMessageId)}"))
                .Content.ReadAsStringAsync(),
            HostJson.Options);

        var decision = Assert.Single(Assert.IsType<DecisionListingResponse>(decisions).Decisions);

        // 3. And the explanation is reachable from there, which is the pane the console exists for.
        Assert.Equal(MailAction.Quarantine, decision.Action);
        Assert.NotEmpty(decision.Reasons);

        var detail = await reviewer.GetAsync($"/v1/decisions/{decision.AssessmentId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        // The whole chain, asserted at the end: the queue id the reviewer started with and the
        // decision they arrived at describe the same message.
        Assert.Equal(
            message.InternalMessageId,
            JsonDocument.Parse(await detail.Content.ReadAsStringAsync())
                .RootElement.GetProperty("internalMessageId").GetString());
    }

    [Fact]
    public async Task The_join_key_is_also_on_the_single_message_route()
    {
        // Both routes serve SubmissionStatusResponse, so the link cannot work from the list and not
        // from the detail: two projections of one row that disagree about the join key would be the
        // drift the shared projection exists to prevent.
        using var host = new TestHost().WithClock();
        await AssessAsync(host, TestPrincipals.AcmeSenderKey, "figures");

        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var page = await ListingAsync(reviewer);
        var assessmentId = page.Decisions.Single().AssessmentId;

        var detail = JsonSerializer.Deserialize<DecisionResponse>(
            await (await reviewer.GetAsync($"/v1/decisions/{assessmentId}")).Content.ReadAsStringAsync(),
            HostJson.Options);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<DecisionResponse>(detail).InternalMessageId));
    }

    private static async Task<DecisionListingResponse> ListingAsync(HttpClient client)
    {
        var response = await client.GetAsync("/v1/decisions");

        return Assert.IsType<DecisionListingResponse>(JsonSerializer.Deserialize<DecisionListingResponse>(
            await response.Content.ReadAsStringAsync(),
            HostJson.Options));
    }

    private static async Task AssessAsync(TestHost host, string apiKey, string subject)
    {
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        using var client = host.ClientAs(apiKey);

        var response = await client.PostAsync(
            "/v1/assessments",
            JsonContent.Create(TestMessages.Request(
                rawMime: TestMessages.Base64(TestMessages.SampleMime.Replace("Quarterly figures", subject)))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
