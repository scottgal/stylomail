using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// The two listings the console's first two screens are built on.
/// </summary>
/// <remarks>
/// <c>GET /v1/senders</c> and <c>GET /v1/messages</c>, both landed by
/// <c>ingress-</c> and both read from the Host's own source rather than from a
/// description of it. The details worth pinning are the ones a route can get
/// subtly wrong without anything going red: which dispositions the messages
/// route actually enumerates, that neither listing takes a tenant, and that a
/// cursor is echoed back rather than invented.
/// </remarks>
public sealed class ListingTests
{
    private static StyloMailApiClient Client(StubHttpMessageHandler handler)
        => new(handler.CreateClient(), new TestApiKeyProvider());

    // ===================== senders =====================

    [Fact]
    public async Task GetSendersAsync_requests_the_senders_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderListing);

        await Client(handler).GetSendersAsync();

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal("/v1/senders", handler.SingleRequest.PathAndQuery);
    }

    /// <summary>
    /// Neither listing takes a tenant. The Host reads it from the authenticated
    /// principal, which is why a cross-tenant read is absent rather than
    /// refused, so there is no parameter for the console to send or to get
    /// wrong.
    /// </summary>
    [Fact]
    public async Task Neither_listing_sends_a_tenant()
    {
        var senders = StubHttpMessageHandler.ReturningJson(Wire.SenderListing);
        var messages = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        await Client(senders).GetSendersAsync();
        await Client(messages).GetMessagesAsync(MessageListState.Held);

        Assert.DoesNotContain("tenant", senders.SingleRequest.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", messages.SingleRequest.PathAndQuery, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A paused principal, one that was paused and resumed, and one no control
    /// action has ever touched, each bound as the Host sends them.
    /// </summary>
    /// <remarks>
    /// The second and third are the pair worth distinguishing. Both arrive
    /// <c>paused: false</c>, and they are not the same history: the resumed one
    /// keeps its audit trail, because "why was this account stopped for six
    /// hours" is asked after the pause is lifted. A console that cleared those
    /// fields on resume, or that treated them as absent, would answer only the
    /// question nobody has.
    /// </remarks>
    [Fact]
    public async Task A_sender_binds_its_control_state_and_audit_trail()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderListing);

        var listing = await Client(handler).GetSendersAsync();

        Assert.Equal("smoke", listing.TenantId);
        Assert.Equal(3, listing.Senders.Count);

        var paused = listing.Senders.Single(sender => sender.PrincipalId == "compromised@example.test");
        Assert.True(paused.Control.Paused);
        Assert.Equal("credential stuffing from this account", paused.Control.Reason);
        Assert.Equal("admin@example.test", paused.Control.UpdatedBy);
        Assert.NotNull(paused.Control.PausedAt);
        Assert.Null(paused.Control.ResumedAt);

        var resumed = listing.Senders.Single(sender => sender.PrincipalId == "quiet@example.test");
        Assert.False(resumed.Control.Paused);
        Assert.Equal("cleared by review", resumed.Control.ResumeReason);
        Assert.Equal("admin@example.test", resumed.Control.ResumedBy);
        Assert.NotNull(resumed.Control.ResumedAt);

        // The pause reason survives the resume, which is the whole point.
        Assert.Equal("held for review", resumed.Control.Reason);
        Assert.True(resumed.Control.HasControlHistory);

        var untouched = listing.Senders.Single(sender => sender.PrincipalId == "untouched@example.test");
        Assert.False(untouched.Control.Paused);

        // Null rather than defaulted: none of it happened, and there is nothing
        // to show rather than an empty audit trail.
        Assert.Null(untouched.Control.Reason);
        Assert.Null(untouched.Control.PausedAt);
        Assert.Null(untouched.Control.UpdatedBy);
        Assert.False(untouched.Control.HasControlHistory);
    }

    // ===================== messages =====================

    [Fact]
    public async Task GetMessagesAsync_requests_the_messages_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        await Client(handler).GetMessagesAsync(MessageListState.Quarantined);

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal("/v1/messages", handler.SingleRequest.Path);
    }

    /// <summary>
    /// The three dispositions the route actually enumerates, and only those.
    /// </summary>
    /// <remarks>
    /// <b>The reason this is a closed type rather than a string.</b> The Host
    /// refuses <c>state=queued</c> by name, deliberately: the queue lists what
    /// is awaiting a decision, not what has been accepted, and there is no
    /// filter for mail in normal delivery. A client that passed an arbitrary
    /// string could ask for one of those and get a 400 at runtime; a client
    /// that can only name these three cannot express the request at all, so the
    /// mistake is caught at the call site rather than against a live Host.
    /// </remarks>
    [Theory]
    [InlineData(MessageListState.AwaitingDecision, "awaiting_decision")]
    [InlineData(MessageListState.Held, "held")]
    [InlineData(MessageListState.Quarantined, "quarantined")]
    public async Task The_state_travels_as_the_wire_name(MessageListState state, string wireName)
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        await Client(handler).GetMessagesAsync(state);

        Assert.Contains($"state={wireName}", handler.SingleRequest.PathAndQuery, StringComparison.Ordinal);
    }

    /// <summary>A first page asks for a page and nothing else.</summary>
    [Fact]
    public async Task The_first_page_sends_no_cursor()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        await Client(handler).GetMessagesAsync(MessageListState.Held, limit: 50);

        Assert.Contains("limit=50", handler.SingleRequest.PathAndQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("after=", handler.SingleRequest.PathAndQuery, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cursor is the Host's own opaque value, echoed back unchanged. It is
    /// never parsed, re-encoded or derived from a row: it carries no tenant and
    /// is not a capability, so treating it as anything but a token to hand back
    /// is how a paging bug gets invented on the client.
    /// </summary>
    [Fact]
    public async Task A_cursor_is_echoed_back_unchanged()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        await Client(handler).GetMessagesAsync(MessageListState.Held, cursor: "cursor+with/specials=");

        Assert.Contains(
            $"after={Uri.EscapeDataString("cursor+with/specials=")}",
            handler.SingleRequest.PathAndQuery,
            StringComparison.Ordinal);
    }

    /// <summary>Rows are the same projection the single-submission route serves.</summary>
    [Fact]
    public async Task Message_rows_bind_as_submission_status()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);

        var listing = await Client(handler).GetMessagesAsync(MessageListState.Held);

        Assert.Equal("held", listing.State);
        Assert.Equal(2, listing.Messages.Count);

        var first = listing.Messages[0];
        Assert.Equal("q_5a2f", first.QueueId);
        Assert.Equal(DeliveryState.Held, first.State);
        Assert.Equal(2, first.Recipients.Count);

        // Per recipient, never per transaction: two recipients of one message
        // are routinely in different states, and collapsing them would hide the
        // case that matters.
        Assert.Equal(DeliveryState.Held, first.Recipients[0].State);
        Assert.Equal(DeliveryState.Delivered, first.Recipients[1].State);
    }

    /// <summary>
    /// Paging state is bound rather than assumed, because the defect that was
    /// just fixed upstream produced exactly this: a second page that came back
    /// empty with hasMore false, presenting a partial set as the whole one.
    /// </summary>
    [Fact]
    public async Task Paging_state_binds_and_a_last_page_has_no_cursor()
    {
        var withMore = StubHttpMessageHandler.ReturningJson(Wire.MessageListing);
        var first = await Client(withMore).GetMessagesAsync(MessageListState.Held);

        Assert.True(first.HasMore);
        Assert.Equal("cursor_page_2", first.NextCursor);

        var lastPage = StubHttpMessageHandler.ReturningJson(Wire.MessageListingLastPage);
        var last = await Client(lastPage).GetMessagesAsync(MessageListState.Held, cursor: "cursor_page_2");

        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
    }

    /// <summary>
    /// A refused state still surfaces the Host's own code, so a console bug
    /// reads as a returned error rather than as an empty list.
    /// </summary>
    [Fact]
    public async Task An_unknown_state_from_the_host_is_a_named_refusal()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("unknown_state", "'queued' is not a state this host lists."),
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetMessagesAsync(MessageListState.Held));

        Assert.Equal("unknown_state", exception.Code);
        Assert.Equal(HttpStatusCode.BadRequest, exception.Status);
    }
}
