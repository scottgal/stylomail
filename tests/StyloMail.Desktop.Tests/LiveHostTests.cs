using System.Net;
using StyloMail.Desktop.Api;
using StyloMail.Desktop.Api.Contracts;

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
    /// The two listings the console's first two screens are built on.
    /// </summary>
    /// <remarks>
    /// This test began life as a tripwire asserting both routes returned 404,
    /// which they did until <c>ingress-</c> wrote them. It failed the moment
    /// they landed, which was its job, and it is now the positive assertion it
    /// was waiting to become.
    /// </remarks>
    [LiveHostFact]
    public async Task The_two_listings_are_reachable_and_bind()
    {
        var client = Client();

        var senders = await client.GetSendersAsync();
        Assert.False(string.IsNullOrEmpty(senders.TenantId));
        Assert.NotNull(senders.Senders);

        var messages = await client.GetMessagesAsync(MessageListState.AwaitingDecision);
        Assert.Equal("awaiting_decision", messages.State);
        Assert.NotNull(messages.Messages);
    }

    /// <summary>
    /// The Host refuses <c>state=queued</c> by name, and that refusal is what
    /// justifies the client's closed state type rather than a free string.
    /// </summary>
    /// <remarks>
    /// Sent through a raw <see cref="HttpClient"/> precisely because the typed
    /// client cannot express it: the value is unrepresentable on this side, and
    /// the point of the test is that the Host agrees it should be.
    /// </remarks>
    [LiveHostFact]
    public async Task The_host_refuses_a_state_the_client_cannot_name()
    {
        var url = Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!;
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            StyloMailApiClient.ApiKeyHeaderName,
            Environment.GetEnvironmentVariable(EnvironmentApiKeyProvider.KeyVariable));

        var response = await http.GetAsync("/v1/messages?state=queued");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown_state", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither listing takes a tenant, and the Host scopes both from the
    /// authenticated principal. Asserted against a live Host because it is a
    /// property of the pair: the client must not send one and the route must
    /// not want one.
    /// </summary>
    [LiveHostFact]
    public async Task Neither_listing_needs_a_tenant_from_the_client()
    {
        var client = Client();

        var senders = await client.GetSendersAsync();
        var messages = await client.GetMessagesAsync(MessageListState.Quarantined);

        // Both answered, and both answered for the caller's own tenant, which
        // the Host read from the key rather than from anything sent.
        Assert.False(string.IsNullOrEmpty(senders.TenantId));
        Assert.Equal(senders.TenantId, messages.TenantId);
    }
}
