using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace StyloMail.Embeddings;

/// <summary>
/// Talks to the local embedding service over loopback HTTP and refuses anything that is not the
/// contract it was configured for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail closed, with a name.</b> Every way this can fail produces <c>NotProduced</c> rows carrying
/// one reason from <see cref="EmbeddingNotProducedReason"/>, and no path returns a zero vector, an
/// empty success or a vector of another width. A caller can therefore store the absence and say why,
/// which is what the derived-signal model needs: absence is a state, not a zero.
/// </para>
/// <para>
/// <b>Four verifications happen before a vector is trusted.</b> The service's model tag, dimension and
/// prefix version must equal the configured ones; its dtype must be bfloat16 or float32 and never
/// float16; each vector must carry the configured width; and each must be finite and unit length
/// within tolerance. The last one is the card's own warning made checkable: a truncated vector that
/// was not renormalised produces plausible wrong scores rather than an error.
/// </para>
/// <para>
/// <b>No retry storm.</b> One retry, only for a 503, plus a breaker that opens after repeated
/// failures. A local service that is down is a state for the caller to record, not something to
/// spend a delivery path's latency budget discovering repeatedly.
/// </para>
/// </remarks>
public sealed partial class SegmentEmbeddingProvider : ISegmentEmbeddingProvider, IElementClassifier
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const float NormTolerance = 1e-4f;

    /// <summary>
    /// Named floating point literals are accepted so a degraded vector arrives as a value this
    /// provider can reject by name. Refusing to parse it would report a contract violation instead,
    /// which hides the one fault the card warns about: a NaN produced by a dtype that cannot carry
    /// the activations.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SegmentEmbeddingProvider>? _logger;
    private readonly EmbeddingCircuitBreaker _breaker;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private EmbeddingMetadata? _metadata;
    private DateTimeOffset _metadataValidatedAt = DateTimeOffset.MinValue;
    private EmbeddingNotProducedReason _lastFailure = EmbeddingNotProducedReason.MetadataUnavailable;

    public SegmentEmbeddingProvider(
        HttpClient http,
        EmbeddingOptions options,
        TimeProvider? timeProvider = null,
        ILogger<SegmentEmbeddingProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _breaker = new EmbeddingCircuitBreaker(
            options.CircuitBreakerFailureThreshold,
            options.CircuitBreakerOpenDuration,
            _time);
    }

    /// <inheritdoc />
    public EmbeddingMetadata? Metadata => _metadata;

    /// <inheritdoc />
    public async Task<EmbeddingMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMetadataAsync(force: true, cancellationToken).ConfigureAwait(false);
        return _metadata;
    }

    /// <inheritdoc />
    public async Task<SegmentEmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return new SegmentEmbeddingBatch(Array.Empty<SegmentEmbedding>(), _metadata);
        }

        if (GuardBatch(segments) is { } guardReason)
        {
            return Absent(segments.Count, guardReason);
        }

        if (_breaker.IsOpen)
        {
            _logger?.LogDebug("embedding skipped: circuit open");
            return Absent(segments.Count, EmbeddingNotProducedReason.CircuitOpen);
        }

        var (token, tokenReason) = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Absent(segments.Count, tokenReason);
        }

        if (await EnsureMetadataAsync(force: false, cancellationToken).ConfigureAwait(false) is null)
        {
            return Absent(segments.Count, _lastFailure);
        }

        for (var attempt = 0; ; attempt++)
        {
            (HttpStatusCode Status, string Body) response;
            try
            {
                response = await SendAndReadAsync(
                    () => BuildEmbedRequest(segments, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddingCallFault fault)
            {
                return Absent(segments.Count, fault.Reason);
            }

            {
                if (response.Status == HttpStatusCode.ServiceUnavailable && attempt < _options.MaxRetries)
                {
                    await Task.Delay(_options.RetryBaseDelay, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return await InterpretAsync(response.Status, response.Body, segments.Count, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private EmbeddingNotProducedReason? GuardBatch(IReadOnlyList<string> segments)
    {
        if (segments.Count > _options.MaxSegments)
        {
            return EmbeddingNotProducedReason.TooManySegments;
        }

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return EmbeddingNotProducedReason.EmptySegment;
            }

            if (segment.Length > _options.MaxSegmentCharacters)
            {
                return EmbeddingNotProducedReason.SegmentTooLong;
            }
        }

        return null;
    }

    private async Task<EmbeddingMetadata?> EnsureMetadataAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _metadata is not null &&
            _time.GetUtcNow() - _metadataValidatedAt < _options.MetadataValidationInterval)
        {
            return _metadata;
        }

        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _metadata is not null &&
                _time.GetUtcNow() - _metadataValidatedAt < _options.MetadataValidationInterval)
            {
                return _metadata;
            }

            (HttpStatusCode Status, string Body) response;
            try
            {
                var (metadataToken, tokenReason) = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
                if (metadataToken is null)
                {
                    return MetadataRefused(tokenReason);
                }

                response = await SendAndReadAsync(() => BuildMetadataRequest(metadataToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EmbeddingCallFault fault)
            {
                return MetadataRefused(fault.Reason);
            }

            {
                if (response.Status == HttpStatusCode.Unauthorized)
                {
                    return MetadataRefused(EmbeddingNotProducedReason.Unauthorized);
                }

                if (!IsSuccess(response.Status))
                {
                    _breaker.RecordFailure();
                    return MetadataRefused(EmbeddingNotProducedReason.MetadataUnavailable);
                }

                var document = ReadDocument<MetadataResponse>(response.Body);
                if (document is null)
                {
                    return MetadataRefused(EmbeddingNotProducedReason.MetadataUnavailable);
                }

                var mismatch = Compare(document);
                if (mismatch is { } reason)
                {
                    _logger?.LogWarning(
                        "embedding service metadata rejected: {Reason}; expected tag={Tag} dim={Dimension} prefix={Prefix}",
                        reason.ToWireName(), _options.ModelTag, _options.Dimension, _options.PrefixVersion);
                    return MetadataRefused(reason);
                }

                _metadata = new EmbeddingMetadata(
                    document.ModelTag!,
                    document.ModelRevision,
                    document.Dimension,
                    document.NativeDimension,
                    document.PrefixVersion!,
                    document.Dtype!,
                    document.Contract ?? "unknown");
                _metadataValidatedAt = _time.GetUtcNow();
                _lastFailure = EmbeddingNotProducedReason.None;
                _breaker.RecordSuccess();
                return _metadata;
            }
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private EmbeddingMetadata? MetadataRefused(EmbeddingNotProducedReason reason)
    {
        _lastFailure = reason;
        _metadata = null;
        _metadataValidatedAt = DateTimeOffset.MinValue;
        return null;
    }

    private EmbeddingNotProducedReason? Compare(MetadataResponse document)
    {
        if (!string.Equals(document.ModelTag, _options.ModelTag, StringComparison.Ordinal))
        {
            return EmbeddingNotProducedReason.ModelTagMismatch;
        }

        if (document.Dimension != _options.Dimension || document.NativeDimension < document.Dimension)
        {
            return EmbeddingNotProducedReason.DimensionMismatch;
        }

        if (!string.Equals(document.PrefixVersion, _options.PrefixVersion, StringComparison.Ordinal))
        {
            return EmbeddingNotProducedReason.PrefixVersionMismatch;
        }

        if (!IsAcceptedDtype(document.Dtype))
        {
            return EmbeddingNotProducedReason.UnsupportedDtype;
        }

        return null;
    }

    private static bool IsAcceptedDtype(string? dtype) =>
        string.Equals(dtype, "bfloat16", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(dtype, "float32", StringComparison.OrdinalIgnoreCase);

    private async Task<(string? Token, EmbeddingNotProducedReason Reason)> ReadTokenAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_options.TokenFilePath))
            {
                return (null, EmbeddingNotProducedReason.TokenUnavailable);
            }

            var text = (await File.ReadAllTextAsync(_options.TokenFilePath, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return text.Length < 16
                ? (null, EmbeddingNotProducedReason.TokenUnavailable)
                : (text, EmbeddingNotProducedReason.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, EmbeddingNotProducedReason.TokenUnavailable);
        }
    }

    private HttpRequestMessage BuildMetadataRequest(string token) => Authorise(
        new HttpRequestMessage(HttpMethod.Get, Combine("metadata")), token);

    private HttpRequestMessage BuildEmbedRequest(IReadOnlyList<string> segments, string token) => Authorise(
        new HttpRequestMessage(HttpMethod.Post, Combine("embed"))
        {
            Content = JsonContent.Create(new EmbedRequest { Segments = segments }, options: JsonOptions),
        },
        token);

    private static HttpRequestMessage Authorise(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private Uri Combine(string relative) =>
        new(new Uri(_options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute), relative);

    private async Task<(HttpStatusCode Status, string Body)> SendAndReadAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        // One deadline for the whole exchange. The previous version created it inside the send and
        // disposed it when SendAsync returned, so the body read ran under the caller's token only:
        // a server could send headers and then stall forever, pinning the analysis path. The read is
        // also bounded as it streams, because Content-Length is optional and absent means unlimited.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        try
        {
            using var response = await _http
                .SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new EmbeddingCallFault(EmbeddingNotProducedReason.ContractViolation);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token)
                .ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            using var memory = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (memory.Length + read > MaxResponseBytes)
                {
                    throw new EmbeddingCallFault(EmbeddingNotProducedReason.ContractViolation);
                }

                memory.Write(buffer, 0, read);
            }

            return (response.StatusCode, Encoding.UTF8.GetString(memory.ToArray()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _breaker.RecordFailure();
            throw new EmbeddingCallFault(EmbeddingNotProducedReason.TimedOut);
        }
        catch (HttpRequestException)
        {
            _breaker.RecordFailure();
            throw new EmbeddingCallFault(EmbeddingNotProducedReason.ServiceUnreachable);
        }
    }

    private async Task<SegmentEmbeddingBatch> InterpretAsync(
        HttpStatusCode status,
        string body,
        int count,
        CancellationToken cancellationToken)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            return Absent(count, EmbeddingNotProducedReason.Unauthorized);
        }

        if (status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
        {
            return Absent(count, EmbeddingNotProducedReason.RequestRejected);
        }

        if (!IsSuccess(status))
        {
            _breaker.RecordFailure();
            return Absent(count, EmbeddingNotProducedReason.ServiceUnavailable);
        }

        var document = ReadDocument<EmbedResponse>(body);
        if (document is null)
        {
            _breaker.RecordFailure();
            return Absent(count, EmbeddingNotProducedReason.ContractViolation);
        }

        if (!string.Equals(document.ModelTag, _options.ModelTag, StringComparison.Ordinal))
        {
            return Absent(count, EmbeddingNotProducedReason.ModelTagMismatch);
        }

        if (!string.Equals(document.PrefixVersion, _options.PrefixVersion, StringComparison.Ordinal))
        {
            return Absent(count, EmbeddingNotProducedReason.PrefixVersionMismatch);
        }

        if (!IsAcceptedDtype(document.Dtype))
        {
            return Absent(count, EmbeddingNotProducedReason.UnsupportedDtype);
        }

        if (document.Dimension != _options.Dimension)
        {
            return Absent(count, EmbeddingNotProducedReason.DimensionMismatch);
        }

        if (document.Normalized != true || document.Vectors is null || document.Vectors.Count != count)
        {
            _breaker.RecordFailure();
            return Absent(count, EmbeddingNotProducedReason.ContractViolation);
        }

        var rows = new List<SegmentEmbedding>(count);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var vector = document.Vectors[ordinal];
            if (vector.Length != _options.Dimension)
            {
                return Absent(count, EmbeddingNotProducedReason.DimensionMismatch);
            }

            double sumOfSquares = 0;
            var finite = true;
            foreach (var value in vector)
            {
                if (!float.IsFinite(value))
                {
                    finite = false;
                    break;
                }

                sumOfSquares += value * value;
            }

            if (!finite)
            {
                return Absent(count, EmbeddingNotProducedReason.NonFiniteVector);
            }

            var norm = Math.Sqrt(sumOfSquares);
            if (Math.Abs(norm - 1.0) > NormTolerance)
            {
                return Absent(count, EmbeddingNotProducedReason.VectorNotNormalised);
            }

            rows.Add(SegmentEmbedding.Produced(ordinal, vector));
        }

        _breaker.RecordSuccess();
        return new SegmentEmbeddingBatch(rows, _metadata);
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static T? ReadDocument<T>(string body) where T : class
    {
        if (body.Length > MaxResponseBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static SegmentEmbeddingBatch Absent(int count, EmbeddingNotProducedReason reason)
    {
        var rows = new SegmentEmbedding[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            rows[ordinal] = SegmentEmbedding.Absent(ordinal, reason);
        }

        return new SegmentEmbeddingBatch(rows, null);
    }

    /// <summary>A transport fault, raised internally so one place maps it to a reason.</summary>
    private sealed class EmbeddingCallFault : Exception
    {
        public EmbeddingCallFault(EmbeddingNotProducedReason reason) => Reason = reason;

        public EmbeddingNotProducedReason Reason { get; }
    }

    private sealed record EmbedRequest
    {
        [JsonPropertyName("segments")]
        public required IReadOnlyList<string> Segments { get; init; }
    }

    private sealed record EmbedResponse
    {
        [JsonPropertyName("vectors")]
        public List<float[]>? Vectors { get; init; }

        [JsonPropertyName("dimension")]
        public int Dimension { get; init; }

        [JsonPropertyName("model_tag")]
        public string? ModelTag { get; init; }

        [JsonPropertyName("prefix_version")]
        public string? PrefixVersion { get; init; }

        [JsonPropertyName("dtype")]
        public string? Dtype { get; init; }

        [JsonPropertyName("normalized")]
        public bool? Normalized { get; init; }
    }

    private sealed record MetadataResponse
    {
        [JsonPropertyName("model_tag")]
        public string? ModelTag { get; init; }

        [JsonPropertyName("model_revision")]
        public string? ModelRevision { get; init; }

        [JsonPropertyName("dimension")]
        public int Dimension { get; init; }

        [JsonPropertyName("native_dimension")]
        public int NativeDimension { get; init; }

        [JsonPropertyName("prefix_version")]
        public string? PrefixVersion { get; init; }

        [JsonPropertyName("dtype")]
        public string? Dtype { get; init; }

        [JsonPropertyName("contract")]
        public string? Contract { get; init; }
    }
}
