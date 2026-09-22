using System.Net;
using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// How a call that did not produce an answer is reported, and what it may not contain.
/// </summary>
/// <remarks>
/// These are the paths an operator actually meets, and the reason they are
/// spelled out is that a console which cannot tell "no key", "refused" and
/// "unreachable" apart is a console whose errors all read the same. Each of the
/// three has a different remedy.
/// </remarks>
public sealed class ApiFailureTests
{
    private const string DecisionId = "asm_0f4d2a";

    private static StyloMailApiClient Client(StubHttpMessageHandler handler, string? key = TestApiKeyProvider.SampleKey)
        => new(handler.CreateClient(), new TestApiKeyProvider(key));

    /// <summary>
    /// The code is the machine-readable half. The console branches on it, and
    /// never on the Host's sentence, which is prose the Host is free to reword.
    /// </summary>
    [Fact]
    public async Task A_refusal_carries_the_hosts_code_and_status()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("decision_not_found", "No decision with that identifier is recorded for this tenant."),
            HttpStatusCode.NotFound);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.HostRefused, exception.Failure);
        Assert.Equal(HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("decision_not_found", exception.Code);
        Assert.Equal(
            "No decision with that identifier is recorded for this tenant.",
            exception.Detail);
    }

    /// <summary>
    /// A storage failure is a 503, and the console must not read it as "the
    /// message was refused": nothing was decided, and the operation may work if
    /// it is tried again. The status is preserved for exactly that reason.
    /// </summary>
    [Fact]
    public async Task A_storage_failure_is_reported_with_its_own_code()
    {
        var handler = StubHttpMessageHandler.ReturningJson(
            Wire.Error("storage_unavailable", "The decision could not be durably recorded."),
            HttpStatusCode.ServiceUnavailable);

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionAsync(DecisionId));

        Assert.Equal("storage_unavailable", exception.Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.Status);
    }

    /// <summary>
    /// A gateway or proxy in front of the Host answers with HTML, not with the
    /// Host's error shape. The status is still the most useful thing known, and
    /// replacing it with a JSON parse exception about a body nobody claimed was
    /// JSON would throw away the only signal there was.
    /// </summary>
    [Fact]
    public async Task A_non_json_error_body_still_reports_its_status()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>"),
        });

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.HostRefused, exception.Failure);
        Assert.Equal(HttpStatusCode.BadGateway, exception.Status);
        Assert.Null(exception.Code);
    }

    /// <summary>
    /// A transport failure is not a refusal: nothing was answered. The
    /// distinction is the difference between "the Host disagrees with this
    /// request" and "the Host was never reached", and the remedy differs.
    /// </summary>
    [Fact]
    public async Task An_unreachable_host_is_distinguished_from_a_refusal()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("Connection refused."));

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.Unreachable, exception.Failure);
        Assert.Null(exception.Status);
    }

    /// <summary>
    /// The stub is built to fail if it is reached, so this test also proves the
    /// stronger claim: not merely that the call reports a missing key, but that
    /// <b>no request left the process</b>. A console that discovered it had no
    /// credential by sending one anyway would put an unauthenticated request in
    /// the Host's logs every time the operator touched a pane.
    /// </summary>
    [Fact]
    public async Task A_missing_api_key_sends_no_request_at_all()
    {
        var handler = StubHttpMessageHandler.Unreachable();

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler, key: null).GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.ApiKeyNotConfigured, exception.Failure);
        Assert.Null(exception.Status);
        Assert.Empty(handler.Requests);
    }

    /// <summary>An empty string is not a key either, and is treated the same way.</summary>
    [Fact]
    public async Task A_blank_api_key_sends_no_request_at_all()
    {
        var handler = StubHttpMessageHandler.Unreachable();

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler, key: "   ").GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.ApiKeyNotConfigured, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Spec 10.3: the key is never in a config file, never in a log, never in a
    /// view. An exception message is all three at once, since it is logged,
    /// shown and screenshotted. This asserts the whole rendered exception, not
    /// just <see cref="Exception.Message"/>, because the stack trace and any
    /// inner exception are rendered too.
    /// </summary>
    [Theory]
    [InlineData("sk-operator-9f3c2b7a1e4d")]
    public async Task A_failure_never_renders_the_api_key(string key)
    {
        var refused = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(Wire.Error("forbidden", "The principal lacks the review privilege.")),
        });

        var refusal = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(refused, key).GetDecisionAsync(DecisionId));

        var unreachable = new StubHttpMessageHandler(
            _ => throw new HttpRequestException($"Connection to https://host.test failed."));

        var transport = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(unreachable, key).GetDecisionAsync(DecisionId));

        Assert.DoesNotContain(key, refusal.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, transport.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, transport.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A console pointed at something that is not a StyloMail Host, which is
    /// the ordinary first-run mistake, gets a readable account of it rather
    /// than a serializer exception.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_a_decision_is_reported_as_unreadable()
    {
        var handler = StubHttpMessageHandler.ReturningJson("""{"hello":"world"}""");

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client(handler).GetDecisionAsync(DecisionId));

        Assert.Equal(StyloMailApiFailure.UnreadableResponse, exception.Failure);
    }
}
