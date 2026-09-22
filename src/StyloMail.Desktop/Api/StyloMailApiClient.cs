using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Api;

/// <summary>
/// The console's entire view of StyloMail: a typed client over the Host's HTTP API.
/// </summary>
/// <remarks>
/// <b>This class is the console's only door into the system.</b> Spec 10.1
/// decides that the desktop app talks to the Host's HTTP API and nothing else,
/// and the practical consequences are all here rather than in a policy document:
/// there is no database to open, no component to reference, and no credential
/// held beyond the caller's own API key. If this client cannot do something, the
/// API is missing a route, and the answer is to ask for the route rather than to
/// reach past it. A headless deployment needs that route too.
///
/// <para>
/// Nothing in this file touches Avalonia. It has no dispatcher, no observable
/// state and no view types, which is what lets the contract be tested against a
/// stubbed handler with no display attached.
/// </para>
///
/// <para>
/// <b>The key is never formatted into anything.</b> It is read once per request
/// and placed in one header. No exception, log line or <c>ToString</c> in this
/// class can produce it.
/// </para>
/// </remarks>
public sealed class StyloMailApiClient
{
    /// <summary>Header the Host's API key handler reads. Mirrors <c>ApiKeyAuthenticationHandler</c>.</summary>
    public const string ApiKeyHeaderName = "X-StyloMail-Key";

    /// <summary>
    /// Serializer settings, mirroring <c>Host.Serialization.HostJson</c>.
    /// </summary>
    /// <remarks>
    /// Web defaults give camelCase property names; the enum converter makes
    /// enums travel as names. Both are deliberate on the Host's side and both
    /// are load-bearing here: a stored enum ordinal would change meaning the
    /// day a member is inserted above it, which is the Host's own stated reason
    /// for writing names.
    ///
    /// <para>
    /// The converter's default behaviour is what makes drift loud: an enum
    /// member this build does not know throws rather than deserialising to the
    /// zero member. Without that, a Host that started sending <c>ShadowHold</c>
    /// would arrive here as <c>Allow</c>, and the console would tell an operator
    /// a message was allowed when it was held.
    /// </para>
    /// </remarks>
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _http;
    private readonly IApiKeyProvider _keys;

    public StyloMailApiClient(HttpClient http, IApiKeyProvider apiKeys)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiKeys);

        _http = http;
        _keys = apiKeys;
    }

    /// <summary>
    /// Reads the ledger entry for one decision: evidence, ordered reasons,
    /// versions and coverage.
    /// </summary>
    /// <remarks>
    /// Requires the review privilege, which is a separate grant from sending.
    /// A console holding only a sending key gets a 403 here, correctly, and
    /// that surfaces as <see cref="StyloMailApiFailure.HostRefused"/> with the
    /// Host's own explanation rather than as an empty pane.
    /// </remarks>
    public Task<DecisionResponse> GetDecisionAsync(
        string decisionId,
        CancellationToken cancellationToken = default)
        => SendAsync<DecisionResponse>(
            HttpMethod.Get,
            $"/v1/decisions/{Uri.EscapeDataString(decisionId)}",
            body: null,
            cancellationToken);

    /// <summary>
    /// Lists the principals this tenant can send as, with their pause state.
    /// </summary>
    /// <remarks>
    /// No tenant parameter exists on the route: the Host takes it from the
    /// authenticated principal, which is why a cross-tenant read is absent
    /// rather than refused. There is nothing here for the console to send or to
    /// get wrong.
    /// </remarks>
    public Task<SenderListingResponse> GetSendersAsync(CancellationToken cancellationToken = default)
        => SendAsync<SenderListingResponse>(HttpMethod.Get, "/v1/senders", body: null, cancellationToken);

    /// <summary>
    /// Lists one page of messages awaiting attention.
    /// </summary>
    /// <param name="state">Which disposition. Only the three the route enumerates can be named.</param>
    /// <param name="limit">Page size. Clamped by the queue rather than rejected when too large.</param>
    /// <param name="cursor">
    /// The previous page's <see cref="MessageListingResponse.NextCursor"/>, echoed
    /// back unchanged. Null for the first page.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Messages in normal delivery are not enumerable here</b>, and that is
    /// the route's design rather than a limitation to work around. The queue
    /// lists what needs attention; a client that wanted the rest would need a
    /// filter the Host has not written, and inventing one here would produce a
    /// page that disagrees with the filter it claims to be showing.
    /// </remarks>
    public Task<MessageListingResponse> GetMessagesAsync(
        MessageListState state,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("/v1/messages?state=")
            .Append(Uri.EscapeDataString(WireName(state)));

        if (limit is { } pageSize)
        {
            query.Append("&limit=").Append(pageSize);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            query.Append("&after=").Append(Uri.EscapeDataString(cursor));
        }

        return SendAsync<MessageListingResponse>(HttpMethod.Get, query.ToString(), body: null, cancellationToken);
    }

    /// <summary>
    /// The wire name of a listing state. Mirrors the Host's filter keys, which
    /// are lowercase with underscores.
    /// </summary>
    /// <remarks>
    /// A switch with no default arm, so adding a member to the enum without
    /// deciding its wire name fails to compile rather than silently sending
    /// something the Host will refuse.
    /// </remarks>
    private static string WireName(MessageListState state) => state switch
    {
        MessageListState.AwaitingDecision => "awaiting_decision",
        MessageListState.Held => "held",
        MessageListState.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped listing state."),
    };

    /// <summary>
    /// Reads one submission's recipient-level delivery progress.
    /// </summary>
    /// <remarks>
    /// Answers for an id the console already holds, and there is no route that
    /// enumerates submissions yet, so the console cannot list what is held.
    /// That gap is raised with the Host's owner rather than worked around:
    /// spec 10.1 makes a missing route the API's problem, not the client's.
    /// </remarks>
    public Task<SubmissionStatusResponse> GetSubmissionAsync(
        string queueId,
        CancellationToken cancellationToken = default)
        => SendAsync<SubmissionStatusResponse>(
            HttpMethod.Get,
            $"/v1/submissions/{Uri.EscapeDataString(queueId)}",
            body: null,
            cancellationToken);

    /// <summary>
    /// Releases a message from quarantine. Audited, and requires the review privilege.
    /// </summary>
    /// <remarks>
    /// <b>The actor is not a parameter.</b> The Host takes it from the
    /// authenticated principal, so a client cannot name someone else as having
    /// released a quarantined message. Adding an actor here would be asking for
    /// a field the server is right to ignore.
    /// </remarks>
    public Task<QuarantineReleaseResponse> ReleaseQuarantineAsync(
        string queueId,
        CancellationToken cancellationToken = default)
        => SendAsync<QuarantineReleaseResponse>(
            HttpMethod.Post,
            $"/v1/quarantine/{Uri.EscapeDataString(queueId)}/release",
            body: null,
            cancellationToken);

    /// <summary>
    /// Pauses an authenticated sending principal, stopping their outbound delivery.
    /// </summary>
    /// <remarks>
    /// <paramref name="reason"/> is required here even though the route accepts
    /// an empty one. The reason is the audit record for the intervention, and a
    /// pause applied with none is, months later, indistinguishable from one
    /// nobody explained. A scripted caller can still send none.
    /// </remarks>
    public Task<PauseSenderResponse> PauseSenderAsync(
        string principalId,
        string reason,
        CancellationToken cancellationToken = default)
        => SendAsync<PauseSenderResponse>(
            HttpMethod.Post,
            $"/v1/controls/senders/{Uri.EscapeDataString(principalId)}/pause",
            new PauseSenderRequest { Reason = reason },
            cancellationToken);

    /// <summary>
    /// Lifts a pause. The audited mirror of <see cref="PauseSenderAsync"/>, and
    /// the same privilege: lifting an intervention is no less a control-plane
    /// act than imposing one.
    /// </summary>
    public Task<ResumeSenderResponse> ResumeSenderAsync(
        string principalId,
        string reason,
        CancellationToken cancellationToken = default)
        => SendAsync<ResumeSenderResponse>(
            HttpMethod.Post,
            $"/v1/controls/senders/{Uri.EscapeDataString(principalId)}/resume",
            new PauseSenderRequest { Reason = reason },
            cancellationToken);

    /// <summary>
    /// Records a trusted label against a decision.
    /// </summary>
    /// <remarks>
    /// These labels feed the trusted baseline, and the Host keeps them distinct
    /// from recipient preference. The console must not blur the two either:
    /// "this recipient wanted this promotion" is not "this message was safe".
    /// </remarks>
    public Task<FeedbackResponse> RecordFeedbackAsync(
        FeedbackRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<FeedbackResponse>(
            HttpMethod.Post,
            "/v1/feedback",
            request,
            cancellationToken);

    /// <summary>
    /// Asks the Host whether it can durably accept mail right now.
    /// </summary>
    /// <remarks>
    /// Unauthenticated on both sides, and deliberately so. The Host maps these
    /// routes outside its authorized group because a probe that has to
    /// authenticate cannot do its job, and this client matches that by sending
    /// no credential: attaching a long-lived operator key to a request that
    /// does not need one is exposure bought with nothing.
    ///
    /// <para>
    /// It is also the one call that works before the console is configured,
    /// which is what makes "is the Host up?" answerable during first run.
    /// </para>
    /// </remarks>
    public Task<ReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken = default)
        => SendUnauthenticatedAsync<ReadinessResponse>(
            HttpMethod.Get,
            "/health/ready",
            // Not ready is a 503 carrying the failed checks. That is the answer,
            // not a failure of the call, so it must not be translated into an
            // exception that discards the body explaining it.
            [HttpStatusCode.ServiceUnavailable],
            cancellationToken);

    /// <summary>
    /// Performs one authenticated request, refusing to send it without a key.
    /// </summary>
    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var key = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(key))
        {
            // Nothing is sent. See IApiKeyProvider: discovering that we had no
            // credential by being refused is a request that tells the operator
            // nothing they did not already know, and one more unauthenticated
            // request in the Host's logs.
            throw StyloMailApiException.KeyNotConfigured(path);
        }

        return await ExecuteAsync<T>(
            method,
            path,
            body,
            key,
            accepted: [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs one request with no credential, for the routes that take none.
    /// </summary>
    private Task<T> SendUnauthenticatedAsync<T>(
        HttpMethod method,
        string path,
        IReadOnlyCollection<HttpStatusCode> accepted,
        CancellationToken cancellationToken)
        => ExecuteAsync<T>(method, path, body: null, apiKey: null, accepted, cancellationToken);

    /// <summary>
    /// The one place a request is built and a response is bound.
    /// </summary>
    /// <remarks>
    /// Every route goes through here so that four things cannot be forgotten at
    /// a single call site: the key is read and sent, an <c>Accept</c> header is
    /// set, an unexpected status becomes a typed failure rather than an
    /// exception from deep inside the serializer, and a body this build cannot
    /// read is reported as version skew rather than as a bad request.
    /// </remarks>
    private async Task<T> ExecuteAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        string? apiKey,
        IReadOnlyCollection<HttpStatusCode> accepted,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            // Serialised against the runtime type explicitly, rather than
            // leaving the type to be inferred from the parameter.
            //
            // Measured, not assumed: with `body` declared as object, the
            // generic overload behaves identically today, and a mutation that
            // swapped one for the other broke no test. The reason to be
            // explicit is what happens later. Narrowing this parameter to a
            // base type or an interface is an ordinary refactor, and at that
            // point the inferred overload starts serialising the declared type
            // and silently drops everything below it. A request record whose
            // members are all optional accepts the resulting partial body
            // without complaint, so the failure would be a field that quietly
            // stops being sent.
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw StyloMailApiException.Unreachable(path, ex);
        }

        using (response)
        {
            return await ReadAsync<T>(path, response, accepted, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadAsync<T>(
        string path,
        HttpResponseMessage response,
        IReadOnlyCollection<HttpStatusCode> accepted,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && !accepted.Contains(response.StatusCode))
        {
            var (code, detail) = ReadError(body);
            throw StyloMailApiException.Refused(response.StatusCode, code, detail);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException ex)
        {
            throw StyloMailApiException.Unreadable(path, ex);
        }
    }

    /// <summary>
    /// Pulls the Host's error code and sentence out of an error body.
    /// </summary>
    /// <remarks>
    /// Best effort by design. A failure to parse this must not replace the
    /// failure we already have: a proxy returning an HTML error page, or a
    /// 502 from something that is not StyloMail at all, still has to surface
    /// as the status it was, with the code absent rather than the whole call
    /// throwing a JSON exception about a body nobody claimed was JSON.
    /// </remarks>
    private static (string? Code, string? Detail) ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return (null, null);
            }

            return (
                ReadString(root, "error"),
                ReadString(root, "detail"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
