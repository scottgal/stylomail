using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using StyloMail.Desktop.Api.Contracts;

namespace StyloMail.Desktop.Api.Contracts
{
    /// <summary>
    /// One page of structural conversation rows for the frozen named window.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ProxyConversationsResponse</c> in the Host. It is written here rather than in
    /// <c>Api/Contracts/ProxyContracts.cs</c> because this lane owns new files only; the shape is
    /// still pinned by the tests against a stubbed handler, which is what makes a mirror worth
    /// having.
    /// </remarks>
    public sealed record ProxyConversationsResponse
    {
        public required string TenantId { get; init; }

        public required string Window { get; init; }

        public required DateTimeOffset WindowStart { get; init; }

        public required DateTimeOffset WindowEnd { get; init; }

        /// <summary><c>available</c> or <c>not_produced</c>.</summary>
        public required string Availability { get; init; }

        public required IReadOnlyList<ProxyConversationResponse> Conversations { get; init; }

        public string? NextCursor { get; init; }

        public required bool HasMore { get; init; }
    }

    /// <summary>
    /// A structural thread row: a participant grouping, never a transcript. No subject, body,
    /// header value or participant address is present, and this client never resolves a token.
    /// </summary>
    public sealed record ProxyConversationResponse
    {
        /// <summary>Unpadded base64url, opaque. Shortened for display, never decoded.</summary>
        public required string ThreadToken { get; init; }

        public required string AccountId { get; init; }

        /// <summary>Opaque participant tokens. They are displayed as tokens, not resolved.</summary>
        public required IReadOnlyList<string> ParticipantTokens { get; init; }

        /// <summary>Null means no threading row was recorded; zero is a measured root depth.</summary>
        public required int? ReferenceDepth { get; init; }

        public required DateTimeOffset LastActivityAt { get; init; }

        public required string Availability { get; init; }
    }

    /// <summary>One thread's per-turn measurements, each with its own availability.</summary>
    public sealed record ProxyConversationSignalsResponse
    {
        public required string TenantId { get; init; }

        public required string ThreadToken { get; init; }

        public required string Window { get; init; }

        public required DateTimeOffset WindowStart { get; init; }

        public required DateTimeOffset WindowEnd { get; init; }

        public required string Provenance { get; init; }

        public required IReadOnlyList<ProxySignalPointResponse> Points { get; init; }
    }

    /// <summary>One turn's measurement, or an explicit statement that it was not produced.</summary>
    public sealed record ProxySignalPointResponse
    {
        public required DateTimeOffset ObservedAt { get; init; }

        /// <summary>Opaque turn token, displayed shortened and never decoded.</summary>
        public required string TurnToken { get; init; }

        /// <summary>
        /// Nullable on purpose: absent tone is a missing measurement, not a zero. No line may be
        /// drawn through it and no zero may be plotted in its place.
        /// </summary>
        public double? ToneScore { get; init; }

        public required IReadOnlyList<string> WarningMarkers { get; init; }

        public required IReadOnlyList<string> DimensionIds { get; init; }

        /// <summary><c>available</c> or <c>not_produced</c>.</summary>
        public required string Availability { get; init; }

        /// <summary>None on an available point, otherwise NoAnalyserConfigured, UnreadableMessage or UnsupportedContent.</summary>
        public required string AvailabilityReason { get; init; }

        /// <summary>Zero on an available point means a measured clean message, not an absent analysis.</summary>
        public required int MarkerCount { get; init; }
    }

    public sealed record ProxyElementSeriesResponse
    {
        public required string TenantId { get; init; }
        public required string ThreadToken { get; init; }
        public required string Window { get; init; }
        public required DateTimeOffset WindowStart { get; init; }
        public required DateTimeOffset WindowEnd { get; init; }
        public required string Provenance { get; init; }
        public required IReadOnlyList<ProxyElementPointResponse> Points { get; init; }
    }

    public sealed record ProxyElementPointResponse
    {
        public required DateTimeOffset ObservedAt { get; init; }
        public required string TurnToken { get; init; }
        public required string TrafficKind { get; init; }
        public required int Ordinal { get; init; }
        public required IReadOnlyList<ProxyElementScoreResponse> Elements { get; init; }
        public required ProxyToneResponse Tone { get; init; }
    }

    public sealed record ProxyElementScoreResponse
    {
        public required string Element { get; init; }
        public required string Scorer { get; init; }
        public required string ScorerVersion { get; init; }
        public required string VocabularyVersion { get; init; }
        public required string ModelTag { get; init; }
        public required string PrefixVersion { get; init; }
        public required int Dimension { get; init; }
        public double? Score { get; init; }
        public string? NearestExemplarId { get; init; }
        public required string Availability { get; init; }
        public required string AvailabilityReason { get; init; }
    }

    public sealed record ProxyToneResponse
    {
        public required string VocabularyVersion { get; init; }
        public string? ModelTag { get; init; }
        public required string Availability { get; init; }
        public required string AvailabilityReason { get; init; }
        public required IReadOnlyList<ProxyToneLabelResponse> Top { get; init; }
    }

    public sealed record ProxyToneLabelResponse { public required string Label { get; init; } public required double Score { get; init; } }
}

namespace StyloMail.Desktop.Api
{
    /// <summary>
    /// The console's two reads over the proxy conversation contract.
    /// </summary>
    /// <remarks>
    /// A separate client rather than more methods on <see cref="StyloMailApiClient"/>, because this
    /// lane works in new files only while that class belongs to the desktop owner. It deliberately
    /// reuses that class's header name, serializer settings and typed failures, so the two doors
    /// cannot drift into two different wire dialects.
    ///
    /// <para>
    /// <b>The window is not a parameter.</b> The contract accepts exactly one named window,
    /// <c>last_24_hours</c>, and the Host refuses anything else. Offering a window argument would
    /// offer a value the server discards or refuses, so the only window this client can send is the
    /// one the contract defines.
    /// </para>
    /// </remarks>
    public sealed class ProxyConversationClient
    {
        /// <summary>The one accepted named observation window.</summary>
        public const string Last24Hours = "last_24_hours";

        /// <summary>Largest page the Host will serve.</summary>
        private const int MaximumPageSize = 100;

        /// <summary>Mirrors the Host's bound for a thread token in a path segment.</summary>
        private const int MaximumTokenLength = 512;

        private readonly HttpClient _http;
        private readonly IApiKeyProvider _keys;

        public ProxyConversationClient(HttpClient http, IApiKeyProvider apiKeys)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(apiKeys);

            _http = http;
            _keys = apiKeys;
        }

        /// <summary>
        /// Lists one page of structural conversations in <c>last_24_hours</c>.
        /// </summary>
        /// <param name="limit">Requested page size, clamped to the Host's 1..100.</param>
        /// <param name="after">The previous page's cursor, echoed back unchanged.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        public Task<ProxyConversationsResponse> GetConversationsAsync(
            int limit = 50,
            string? after = null,
            CancellationToken cancellationToken = default)
        {
            var path = "/v1/proxy/conversations?window=" + Last24Hours
                + "&limit=" + Math.Clamp(limit, 1, MaximumPageSize).ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(after) ? string.Empty : "&after=" + Uri.EscapeDataString(after));

            return SendAsync<ProxyConversationsResponse>(path, cancellationToken);
        }

        /// <summary>
        /// Reads one thread's per-turn points in <c>last_24_hours</c>.
        /// </summary>
        /// <remarks>
        /// The token is validated before anything is sent. A token that is not unpadded base64url
        /// could be a path, a query fragment or a value that is not a token at all, and refusing it
        /// here keeps the console from being the thing that puts it on a wire or a screen.
        /// </remarks>
        public Task<ProxyConversationSignalsResponse> GetSignalsAsync(
            string threadToken,
            CancellationToken cancellationToken = default)
        {
            if (!IsThreadToken(threadToken))
            {
                throw new ArgumentException(
                    "A thread token must be unpadded base64url (A-Z, a-z, 0-9, '-' and '_'), at most 512 characters.",
                    nameof(threadToken));
            }

            return SendAsync<ProxyConversationSignalsResponse>(
                "/v1/proxy/conversations/" + Uri.EscapeDataString(threadToken)
                    + "/signals?window=" + Last24Hours,
                cancellationToken);
        }

        /// <summary>Reads content-free element measurements for one thread in the fixed named window.</summary>
        public Task<ProxyElementSeriesResponse> GetElementsAsync(
            string threadToken,
            CancellationToken cancellationToken = default)
        {
            if (!IsThreadToken(threadToken))
            {
                throw new ArgumentException(
                    "A thread token must be unpadded base64url (A-Z, a-z, 0-9, '-' and '_'), at most 512 characters.",
                    nameof(threadToken));
            }

            return SendAsync<ProxyElementSeriesResponse>(
                "/v1/proxy/conversations/" + Uri.EscapeDataString(threadToken)
                    + "/elements?window=" + Last24Hours,
                cancellationToken);
        }

        /// <summary>
        /// Whether a value has the exact shape the Host accepts as a thread token.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>ProxyTokens.IsUrlSafe</c>: the charset, the length bound and the
        /// <c>length % 4 == 1</c> rule that no unpadded base64url string can satisfy. Padding and
        /// <c>+</c>/<c>/</c> are rejected rather than normalised, so two spellings never address one
        /// thread.
        /// </remarks>
        public static bool IsThreadToken(string? token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength || token.Length % 4 == 1)
            {
                return false;
            }

            return IsTokenCharset(token);
        }

        /// <summary>
        /// Whether a value can be displayed as an opaque token without carrying content.
        /// </summary>
        /// <remarks>
        /// The charset alone, without the base64url length rule, because participant tokens are
        /// account identifiers rather than derived thread tokens. The point is narrower and
        /// load-bearing: an address, login, subject or header value cannot pass this test, so a
        /// row carrying one is refused rather than rendered.
        /// </remarks>
        public static bool IsOpaqueToken(string? token)
            => !string.IsNullOrEmpty(token) && token.Length <= MaximumTokenLength && IsTokenCharset(token);

        private static bool IsTokenCharset(string token)
        {
            foreach (var character in token)
            {
                var allowed = character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_';

                if (!allowed) return false;
            }

            return true;
        }

        private async Task<T> SendAsync<T>(string path, CancellationToken cancellationToken)
        {
            var key = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(key))
            {
                throw StyloMailApiException.KeyNotConfigured(path);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation(StyloMailApiClient.ApiKeyHeaderName, key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var (code, detail) = ReadError(body);
                    throw StyloMailApiException.Refused(response.StatusCode, code, detail);
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(body, StyloMailApiClient.JsonOptions)
                        ?? throw new JsonException("The response body was empty.");
                }
                catch (JsonException ex)
                {
                    throw StyloMailApiException.Unreadable(path, ex);
                }
            }
        }

        /// <summary>Best effort: a failure to parse an error body must not replace the status.</summary>
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

                return (ReadString(root, "error"), ReadString(root, "detail"));
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
    }
}
