using System.Net;
using StyloMail.Desktop.Api;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// A fact that only runs against a real Host.
/// </summary>
/// <remarks>
/// Skipped unless <c>STYLOMAIL_SMOKE_URL</c> names a running instance. The
/// contract tests are hermetic and must stay that way: a suite that needed a
/// server would be skipped in CI and quietly become the suite nobody runs.
/// This is the one opt-in place where the client meets the thing it was written
/// for.
/// </remarks>
public sealed class LiveHostFactAttribute : FactAttribute
{
    public const string UrlVariable = "STYLOMAIL_SMOKE_URL";

    public LiveHostFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)))
        {
            Skip = $"Set {UrlVariable} to a running Host to run this.";
        }
    }
}

/// <summary>
/// Reads the key from the environment.
/// </summary>
/// <remarks>
/// A test utility, and deliberately not the production seam. The console reads
/// its key from the platform keychain and from nowhere else, because spec 10.3
/// forbids a config file and an environment variable is a config file that
/// leaks through a process listing. This exists so that a local smoke run does
/// not have to write into a real operator's keychain.
/// </remarks>
internal sealed class EnvironmentApiKeyProvider : IApiKeyProvider
{
    public const string KeyVariable = "STYLOMAIL_SMOKE_KEY";

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Environment.GetEnvironmentVariable(KeyVariable));
}

/// <summary>
/// The client against a Host that is actually running.
/// </summary>
/// <remarks>
/// What this adds over the stubbed tests is the one thing a stub cannot
/// supply: evidence that the client's idea of the contract is the server's.
/// The stubbed suite proves the client is self-consistent; this proves it is
/// right.
/// </remarks>
public sealed class LiveHostTests
{
    private static StyloMailApiClient Client()
    {
        var url = Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!;

        return new StyloMailApiClient(
            new HttpClient { BaseAddress = new Uri(url) },
            new EnvironmentApiKeyProvider());
    }

    /// <summary>The only call that works before any key is configured.</summary>
    [LiveHostFact]
    public async Task A_running_host_reports_itself_ready()
    {
        var readiness = await Client().GetReadinessAsync();

        Assert.True(readiness.Ready);
        Assert.Equal("ready", readiness.Status);
    }

    /// <summary>
    /// The error shape, against the real thing. The code and the status are the
    /// two halves the console branches on, and both are asserted as literal
    /// values rather than through a constant.
    /// </summary>
    [LiveHostFact]
    public async Task An_unknown_decision_comes_back_as_a_typed_refusal()
    {
        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client().GetDecisionAsync("definitely-not-a-real-id"));

        Assert.Equal(StyloMailApiFailure.HostRefused, exception.Failure);
        Assert.Equal(HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("decision_not_found", exception.Code);
    }

    /// <summary>
    /// A console pointed at a Host with the wrong key. This is the ordinary
    /// first-run mistake, and it must arrive as a refusal rather than as
    /// something that looks like an empty ledger.
    /// </summary>
    [LiveHostFact]
    public async Task A_wrong_key_is_refused()
    {
        var url = Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!;

        var client = new StyloMailApiClient(
            new HttpClient { BaseAddress = new Uri(url) },
            new TestApiKeyProvider("definitely-not-a-valid-key"));

        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => client.GetDecisionAsync("definitely-not-a-real-id"));

        Assert.Equal(StyloMailApiFailure.HostRefused, exception.Failure);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.Status);
    }

    /// <summary>
    /// A tripwire on the Host's surface, and deliberately not a client test.
    /// </summary>
    /// <remarks>
    /// <c>GET /v1/senders</c> and <c>GET /v1/messages</c> do not exist, which is
    /// why the console's sidebar and message list cannot be built yet. This is
    /// asserted through a raw <see cref="HttpClient"/> rather than by giving the
    /// client methods for routes that are not there: a client that shipped a
    /// call to a route nobody has written would be the workaround the mission
    /// rules out, and it would make the gap look closed.
    ///
    /// <para>
    /// When either route is mapped this test fails, and that failure is the
    /// signal to wire up the screen it unblocks. A tripwire that fails when the
    /// work is ready is the point, not a bug in the test.
    /// </para>
    /// </remarks>
    [LiveHostFact]
    public async Task The_sender_and_message_listings_are_not_mapped_yet()
    {
        var url = Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!;
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            StyloMailApiClient.ApiKeyHeaderName,
            Environment.GetEnvironmentVariable(EnvironmentApiKeyProvider.KeyVariable));

        var senders = await http.GetAsync("/v1/senders");
        var messages = await http.GetAsync("/v1/messages");

        Assert.Equal(HttpStatusCode.NotFound, senders.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, messages.StatusCode);
    }
}
