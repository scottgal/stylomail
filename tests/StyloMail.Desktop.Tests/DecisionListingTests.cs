using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The ledger listing, and the join from a message to its decision.
/// </summary>
/// <remarks>
/// This is the first hop of the console's headline flow: a reviewer looking at
/// a quarantined message wants the explanation for it. Until the message rows
/// carried <c>internalMessageId</c> and the ledger could be filtered by it,
/// there was no route from one to the other at all.
/// </remarks>
public sealed class DecisionListingTests
{
    private static StyloMailApiClient Client(StubHttpMessageHandler handler)
        => new(handler.CreateClient(), new TestApiKeyProvider());

    [Fact]
    public async Task GetDecisionsAsync_requests_the_ledger_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        await Client(handler).GetDecisionsAsync();

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal("/v1/decisions", handler.SingleRequest.PathAndQuery);
    }

    /// <summary>
    /// The join. The message listings carry this id, and the ledger narrows by
    /// it, which is how a message reaches its decision.
    /// </summary>
    [Fact]
    public async Task The_ledger_can_be_narrowed_to_one_message()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        await Client(handler).GetDecisionsAsync(messageId: "msg_9c1b7e");

        Assert.Contains("messageId=msg_9c1b7e", handler.SingleRequest.PathAndQuery, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sent as the enum's own name. The Host refuses an action it does not
    /// record by name, so a lowercased guess would be refused rather than
    /// ignored, and would fail at runtime rather than at the call site.
    /// </summary>
    [Theory]
    [InlineData(MailAction.Quarantine, "action=Quarantine")]
    [InlineData(MailAction.Hold, "action=Hold")]
    public async Task An_action_filter_travels_as_the_name(MailAction action, string expected)
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        await Client(handler).GetDecisionsAsync(action: action);

        Assert.Contains(expected, handler.SingleRequest.PathAndQuery, StringComparison.Ordinal);
    }

    /// <summary>An unfiltered listing asks for no filters rather than for empty ones.</summary>
    [Fact]
    public async Task An_unfiltered_listing_sends_no_parameters()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        await Client(handler).GetDecisionsAsync();

        var path = handler.SingleRequest.PathAndQuery;
        Assert.DoesNotContain("messageId", path, StringComparison.Ordinal);
        Assert.DoesNotContain("action", path, StringComparison.Ordinal);
        Assert.DoesNotContain("after", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cursor_is_echoed_back_unchanged()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        await Client(handler).GetDecisionsAsync(cursor: "cursor+with/specials=");

        Assert.Contains(
            $"after={Uri.EscapeDataString("cursor+with/specials=")}",
            handler.SingleRequest.PathAndQuery,
            StringComparison.Ordinal);
    }

    /// <summary>A row carries what a reviewer needs to decide whether to open it.</summary>
    [Fact]
    public async Task A_summary_row_binds_its_reasons_and_coverage()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.DecisionListing);

        var listing = await Client(handler).GetDecisionsAsync(messageId: "msg_9c1b7e");

        Assert.Equal("smoke", listing.TenantId);
        var row = Assert.Single(listing.Decisions);

        Assert.Equal("asm_0f4d2a", row.AssessmentId);
        Assert.Equal("msg_9c1b7e", row.InternalMessageId);
        Assert.Equal(MailAction.Quarantine, row.Action);

        // Ordered, so the row says why it is interesting without being opened.
        Assert.Equal(["credential_request_high", "link_display_mismatch"], row.Reasons.Select(r => r.Code));

        // Coverage on the row, because a decision over less than the whole
        // message is a weaker one and the list should show which rows those are.
        Assert.True(row.Coverage.HtmlTextDisagreement);
        Assert.False(row.Coverage.ContentEncrypted);
    }

    /// <summary>
    /// The row carries no evidence, which is the point of a summary: the full
    /// explanation is one request away and the listing cannot bound evidence
    /// volume per message.
    /// </summary>
    [Fact]
    public async Task A_summary_row_carries_no_evidence()
    {
        var listing = await Client(StubHttpMessageHandler.ReturningJson(Wire.DecisionListing))
            .GetDecisionsAsync();

        // The type has no Evidence member at all, so this is a compile-time
        // property; the assertion is that the reason carries signal ids it can
        // be resolved against once the detail is fetched.
        Assert.NotEmpty(listing.Decisions[0].Reasons[0].EvidenceSignalIds);
    }

    /// <summary>
    /// A message with no decisions gets an empty page, not a 404. "This message
    /// has no decisions" and "I do not know that id" are different facts.
    /// </summary>
    [Fact]
    public async Task A_message_with_no_decisions_is_an_empty_page()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.EmptyDecisionListing);

        var listing = await Client(handler).GetDecisionsAsync(messageId: "msg_unknown");

        Assert.Empty(listing.Decisions);
        Assert.False(listing.HasMore);
        Assert.Null(listing.NextCursor);
    }

    /// <summary>An action the ledger does not record is refused by name.</summary>
    [Fact]
    public async Task An_unknown_action_is_a_named_refusal()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("unknown_action", "'Escalate' is not an action this ledger records."),
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionsAsync(action: MailAction.Hold));

        Assert.Equal("unknown_action", exception.Code);
    }

    /// <summary>
    /// A cursor the ledger did not issue is refused rather than treated as
    /// "start again": silently answering with the first page leaves a client
    /// paging in a loop with nothing saying why.
    /// </summary>
    [Fact]
    public async Task An_invalid_cursor_is_a_named_refusal()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("invalid_cursor", "That is not a cursor this ledger issued."),
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionsAsync(cursor: "made-up"));

        Assert.Equal("invalid_cursor", exception.Code);
    }
}
