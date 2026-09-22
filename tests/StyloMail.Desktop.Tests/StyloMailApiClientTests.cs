using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// What the client puts on the wire: the route, the method, the body and the credential.
/// </summary>
/// <remarks>
/// These are the assertions the Host's own tests cannot make, and they are the
/// half of the contract that breaks silently. A wrong path is a 404 the operator
/// reads as "not found" rather than as a client bug. A missing key is a 401 with
/// no hint that the key was never sent. An enum serialised as a number instead
/// of a name is a 400 the Host reports as a malformed field.
/// </remarks>
public sealed class StyloMailApiClientTests
{
    private const string DecisionId = "asm_0f4d2a";
    private const string QueueId = "q_5a2f";
    private const string SenderId = "sender@example.test";

    private static StyloMailApiClient Client(StubHttpMessageHandler handler, string? key = TestApiKeyProvider.SampleKey)
        => new(handler.CreateClient(), new TestApiKeyProvider(key));

    /// <summary>
    /// The header name is written out literally here rather than through
    /// <see cref="StyloMailApiClient.ApiKeyHeaderName"/>, on purpose: asserting a
    /// constant against its own use proves nothing, and a header renamed to a
    /// typo would send every request out unauthenticated while every other test
    /// in this file still passed.
    /// </summary>
    [Fact]
    public async Task An_authenticated_request_sends_the_api_key_in_the_hosts_header()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Decision);

        await Client(handler).GetDecisionAsync(DecisionId);

        Assert.Equal(
            TestApiKeyProvider.SampleKey,
            handler.SingleRequest.Header("X-StyloMail-Key"));
    }

    /// <summary>
    /// Pins the constant to the literal the Host's <c>ApiKeyAuthenticationHandler</c>
    /// reads.
    /// </summary>
    /// <remarks>
    /// This exists to keep the negative assertions honest. The readiness test
    /// above asserts that no header of that name is sent, and if the constant
    /// were renamed to a typo that assertion would pass for the wrong reason
    /// forever, quietly proving nothing about what went on the wire.
    /// </remarks>
    [Fact]
    public void The_api_key_header_constant_is_the_one_the_host_reads()
    {
        Assert.Equal("X-StyloMail-Key", StyloMailApiClient.ApiKeyHeaderName);
    }

    [Fact]
    public async Task GetDecisionAsync_requests_the_decision_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Decision);

        await Client(handler).GetDecisionAsync(DecisionId);

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal($"/v1/decisions/{DecisionId}", handler.SingleRequest.Path);
    }

    [Fact]
    public async Task GetSubmissionAsync_requests_the_submission_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SubmissionStatus);

        await Client(handler).GetSubmissionAsync(QueueId);

        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);
        Assert.Equal($"/v1/submissions/{QueueId}", handler.SingleRequest.Path);
    }

    [Fact]
    public async Task ReleaseQuarantineAsync_posts_to_the_release_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.QuarantineRelease);

        await Client(handler).ReleaseQuarantineAsync(QueueId);

        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
        Assert.Equal($"/v1/quarantine/{QueueId}/release", handler.SingleRequest.Path);
    }

    [Fact]
    public async Task PauseSenderAsync_posts_to_the_pause_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderPaused);

        await Client(handler).PauseSenderAsync("sender.b", "compromised account");

        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
        Assert.Equal("/v1/controls/senders/sender.b/pause", handler.SingleRequest.Path);
    }

    [Fact]
    public async Task ResumeSenderAsync_posts_to_the_resume_route()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderResumed);

        await Client(handler).ResumeSenderAsync("sender.b", "cleared by review");

        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
        Assert.Equal("/v1/controls/senders/sender.b/resume", handler.SingleRequest.Path);
    }

    /// <summary>
    /// A principal id is an address in this system, so it has to survive the
    /// path. An unescaped <c>@</c> or a slash would silently address a
    /// different route, or a different principal.
    /// </summary>
    [Fact]
    public async Task PauseSenderAsync_escapes_a_principal_id_that_is_an_address()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderPaused);

        await Client(handler).PauseSenderAsync(SenderId, "compromised account");

        Assert.Equal("/v1/controls/senders/sender%40example.test/pause", handler.SingleRequest.Path);
    }

    /// <summary>
    /// The reason is the audit record for the pause. A pause with no reason is
    /// indistinguishable, later, from one nobody explained.
    /// </summary>
    [Fact]
    public async Task PauseSenderAsync_sends_the_reason_in_the_body()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.SenderPaused);

        await Client(handler).PauseSenderAsync(SenderId, "credential stuffing from this account");

        Assert.Contains("credential stuffing from this account", handler.SingleRequest.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Labels travel as names, matching <c>HostJson</c>. An ordinal here would
    /// be a 400 at best, and a silently mislabelled decision at worst.
    /// </summary>
    [Fact]
    public async Task RecordFeedbackAsync_sends_the_label_and_scope_as_names()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.FeedbackRecorded);

        await Client(handler).RecordFeedbackAsync(new FeedbackRequest
        {
            DecisionId = DecisionId,
            Label = FeedbackLabel.Legitimate,
            Scope = FeedbackScope.Recipient,
            Recipient = "alice@example.test",
        });

        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
        Assert.Equal("/v1/feedback", handler.SingleRequest.Path);

        var body = handler.SingleRequest.Body;
        Assert.NotNull(body);
        Assert.Contains("\"label\":\"Legitimate\"", body, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"Recipient\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"decisionId\":\"{DecisionId}\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Health is unauthenticated on the Host, and sending a credential to a
    /// route that does not need one is exposure with nothing bought by it.
    /// </summary>
    [Fact]
    public async Task GetReadinessAsync_does_not_send_the_api_key()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Ready);

        await Client(handler).GetReadinessAsync();

        Assert.Equal("/health/ready", handler.SingleRequest.Path);
        Assert.Null(handler.SingleRequest.Header(StyloMailApiClient.ApiKeyHeaderName));
    }

    /// <summary>
    /// The case that decides whether the first run is usable: an operator
    /// pointing the console at a Host before they have a key must be able to
    /// see that the Host is up.
    /// </summary>
    [Fact]
    public async Task GetReadinessAsync_answers_with_no_api_key_configured()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.Ready);

        var readiness = await Client(handler, key: null).GetReadinessAsync();

        Assert.True(readiness.Ready);
        _ = handler.SingleRequest;
    }

    /// <summary>
    /// A Host that cannot durably accept mail answers 503 with a body naming
    /// the failed checks. That is information, not a failure of this call:
    /// rendering it as an error would turn the one useful answer into a
    /// generic one.
    /// </summary>
    [Fact]
    public async Task GetReadinessAsync_reports_not_ready_rather_than_throwing()
    {
        var handler = StubHttpMessageHandler.ReturningJson(Wire.NotReady, HttpStatusCode.ServiceUnavailable);

        var readiness = await Client(handler).GetReadinessAsync();

        Assert.False(readiness.Ready);
        Assert.Equal("not_ready", readiness.Status);
        Assert.NotNull(readiness.FailedChecks);
        Assert.Contains(readiness.FailedChecks, check => check.Contains("spool", StringComparison.OrdinalIgnoreCase));
    }
}
