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
    /// The ledger listing, and the join from a message to its decisions.
    /// </summary>
    /// <remarks>
    /// The <b>empty</b> case is what is reachable here, and it is the one worth
    /// pinning: a message id the ledger has never seen gets an empty page
    /// rather than a 404, because "this message has no decisions" and "I do not
    /// know that id" are different facts and the ledger can only answer the
    /// first. Populated pages need a decision, which needs a provider key this
    /// suite must not hold.
    /// </remarks>
    [LiveHostFact]
    public async Task The_ledger_answers_an_unknown_message_with_an_empty_page()
    {
        var client = Client();

        var listing = await client.GetDecisionsAsync(messageId: "msg_never_seen");

        Assert.Empty(listing.Decisions);
        Assert.False(listing.HasMore);
        Assert.Null(listing.NextCursor);
    }

    /// <summary>The unfiltered ledger is reachable, and scoped to the caller's tenant.</summary>
    [LiveHostFact]
    public async Task The_ledger_listing_is_reachable()
    {
        var client = Client();

        var listing = await client.GetDecisionsAsync();

        Assert.False(string.IsNullOrEmpty(listing.TenantId));
        Assert.Null(listing.Action);
    }

    /// <summary>
    /// An action the ledger does not record is refused by name, rather than
    /// silently listing everything under a filter that says otherwise.
    /// </summary>
    [LiveHostFact]
    public async Task The_ledger_refuses_an_action_it_does_not_record()
    {
        var url = Environment.GetEnvironmentVariable(LiveHostFactAttribute.UrlVariable)!;
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            StyloMailApiClient.ApiKeyHeaderName,
            Environment.GetEnvironmentVariable(EnvironmentApiKeyProvider.KeyVariable));

        var response = await http.GetAsync("/v1/decisions?action=Escalate");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unknown_action", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The release route's refusal for a message the Host does not hold.
    /// </summary>
    /// <remarks>
    /// <b>What this test can and cannot reach, stated plainly.</b> Releasing a
    /// genuinely quarantined message needs a quarantined message, and there is
    /// no way to produce one without a working semantic provider key: an
    /// assessment needs Jev, and a rejected key currently fails the whole
    /// request rather than degrading to unavailable evidence. The operator's
    /// key is not something this suite may hold.
    ///
    /// <para>
    /// So the console's release path is verified by its own tests against a
    /// stubbed handler, and against a real Host only for the case above, which
    /// is reachable: a refusal naming the reason. The success path has not run
    /// end to end, and this note exists so that is not mistaken for a gap
    /// somebody forgot rather than one that is currently unreachable.
    /// </para>
    /// </remarks>
    [LiveHostFact]
    public async Task Releasing_an_unknown_message_is_refused_by_name()
    {
        var exception = await Assert.ThrowsAsync<StyloMailApiException>(
            () => Client().ReleaseQuarantineAsync("q_not_a_real_queue_id"));

        Assert.Equal(StyloMailApiFailure.HostRefused, exception.Failure);
        Assert.Equal(HttpStatusCode.NotFound, exception.Status);
        Assert.Equal("submission_not_found", exception.Code);
    }

    /// <summary>
    /// The console's whole write path, against a real Host: pause, read it
    /// back, resume, read it back.
    /// </summary>
    /// <remarks>
    /// Reading the state back from the Host rather than trusting the response
    /// is the point. A pause that answered <c>paused: true</c> without anything
    /// changing would pass every stubbed test in this suite and still leave an
    /// operator believing an account had been stopped when it had not.
    ///
    /// <para>
    /// It undoes what it does, so it is safe to run repeatedly against a
    /// throwaway Host. It does write to the Host it is pointed at, which is why
    /// it is opt-in alongside the rest of this file rather than in the default
    /// suite.
    /// </para>
    /// </remarks>
    [LiveHostFact]
    public async Task A_pause_is_applied_read_back_and_then_lifted()
    {
        var client = Client();

        var principal = (await client.GetSendersAsync()).Senders[0].PrincipalId;

        var paused = await client.PauseSenderAsync(principal, "desktop smoke test, pause");
        Assert.True(paused.Paused);
        Assert.Equal(principal, paused.PrincipalId);

        var afterPause = await client
            .GetSendersAsync();

        var pausedRow = afterPause.Senders.Single(sender => sender.PrincipalId == principal);
        Assert.True(pausedRow.Control.Paused);
        Assert.Equal("desktop smoke test, pause", pausedRow.Control.Reason);
        Assert.NotNull(pausedRow.Control.PausedAt);

        var resumed = await client.ResumeSenderAsync(principal, "desktop smoke test, cleanup");
        Assert.False(resumed.Paused);

        var afterResume = await client.GetSendersAsync();
        var resumedRow = afterResume.Senders.Single(sender => sender.PrincipalId == principal);

        Assert.False(resumedRow.Control.Paused);

        // The pause's audit trail survives the resume, which is the property
        // the console renders and the reason its field names are kept.
        Assert.Equal("desktop smoke test, pause", resumedRow.Control.Reason);
        Assert.NotNull(resumedRow.Control.ResumedAt);
        Assert.Equal("desktop smoke test, cleanup", resumedRow.Control.ResumeReason);
    }

    /// <summary>
    /// A pause with no reason is accepted by the route, because a scripted
    /// caller may have nothing to say. The console requires one; the route does
    /// not, and this pins that the difference is the console's policy rather
    /// than a contract the Host enforces.
    /// </summary>
    [LiveHostFact]
    public async Task The_route_itself_accepts_an_empty_reason()
    {
        var client = Client();

        var principal = (await client.GetSendersAsync()).Senders[0].PrincipalId;

        await client.ResumeSenderAsync(principal, string.Empty);

        var resumed = await client.PauseSenderAsync(principal, string.Empty);
        Assert.True(resumed.Paused);

        await client.ResumeSenderAsync(principal, "cleanup");
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
