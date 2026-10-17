using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace StyloMail.Embeddings;

/// <summary>
/// The element classification half of the client. Same service, same token, same breaker.
/// </summary>
/// <remarks>
/// The two calls differ in what they return, not in how they fail: a fault produces one row per
/// (segment, element, scorer) with <see cref="ElementAvailability.NotProduced"/> and a named reason,
/// and no row ever carries a zero in place of a missing score. A zero margin is a real point, the
/// point where the nearest threatening and nearest benign exemplars are equally close.
/// </remarks>
public sealed partial class SegmentEmbeddingProvider
{
    private static readonly IReadOnlyList<string> DefaultScorers = new[] { ElementScorers.PrototypeKnn };

    /// <inheritdoc />
    public async Task<ElementClassification> ClassifyAsync(
        IReadOnlyList<string> segments,
        IReadOnlyList<string>? scorers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var requested = NormaliseScorers(scorers);
        if (segments.Count == 0)
        {
            return new ElementClassification(
                _metadata,
                ElementVocabulary.Version,
                requested.Select(scorer => new ElementScorerResult(
                    scorer, ElementScorers.VersionOf(scorer)!, Array.Empty<SegmentElementScores>())).ToList());
        }

        if (GuardBatch(segments) is { } guardReason)
        {
            return ElementFailure(segments.Count, requested, ToElementReason(guardReason));
        }

        if (_breaker.IsOpen)
        {
            return ElementFailure(segments.Count, requested, ElementNotProducedReason.CircuitOpen);
        }

        var (token, tokenReason) = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return ElementFailure(segments.Count, requested, ToElementReason(tokenReason));
        }

        if (await EnsureMetadataAsync(force: false, cancellationToken).ConfigureAwait(false) is null)
        {
            return ElementFailure(segments.Count, requested, ToElementReason(_lastFailure));
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(
                    () => BuildClassifyRequest(segments, requested, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddingCallFault fault)
            {
                return ElementFailure(segments.Count, requested, ToElementReason(fault.Reason));
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < _options.MaxRetries)
                {
                    await Task.Delay(_options.RetryBaseDelay, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return await InterpretClassifyAsync(response, segments.Count, requested, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<string> NormaliseScorers(IReadOnlyList<string>? scorers)
    {
        if (scorers is null || scorers.Count == 0)
        {
            return DefaultScorers;
        }

        foreach (var scorer in scorers)
        {
            if (!ElementScorers.Contains(scorer))
            {
                throw new ArgumentException(
                    $"'{scorer}' is not a scorer; the closed set is {string.Join(", ", ElementScorers.All)}",
                    nameof(scorers));
            }
        }

        return scorers.Distinct(StringComparer.Ordinal).ToList();
    }

    private HttpRequestMessage BuildClassifyRequest(
        IReadOnlyList<string> segments,
        IReadOnlyList<string> scorers,
        string token) => Authorise(
        new HttpRequestMessage(HttpMethod.Post, Combine("classify"))
        {
            Content = JsonContent.Create(new ClassifyRequest { Segments = segments, Scorers = scorers }),
        },
        token);

    private async Task<ElementClassification> InterpretClassifyAsync(
        HttpResponseMessage response,
        int count,
        IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ElementFailure(count, requested, ElementNotProducedReason.Unauthorized);
        }

        if (response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
        {
            return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
        }

        if (!response.IsSuccessStatusCode)
        {
            _breaker.RecordFailure();
            return ElementFailure(count, requested, ElementNotProducedReason.ServiceUnavailable);
        }

        var document = await ReadDocumentAsync<ClassifyResponse>(response, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            _breaker.RecordFailure();
            return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
        }

        if (!string.Equals(document.ModelTag, _options.ModelTag, StringComparison.Ordinal))
        {
            return ElementFailure(count, requested, ElementNotProducedReason.ModelTagMismatch);
        }

        if (!string.Equals(document.PrefixVersion, _options.PrefixVersion, StringComparison.Ordinal))
        {
            return ElementFailure(count, requested, ElementNotProducedReason.PrefixVersionMismatch);
        }

        if (!IsAcceptedDtype(document.Dtype))
        {
            return ElementFailure(count, requested, ElementNotProducedReason.UnsupportedDtype);
        }

        if (document.Dimension != _options.Dimension)
        {
            return ElementFailure(count, requested, ElementNotProducedReason.DimensionMismatch);
        }

        if (!string.Equals(document.VocabularyVersion, ElementVocabulary.Version, StringComparison.Ordinal)
            || document.Scorers is null)
        {
            _breaker.RecordFailure();
            return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
        }

        var results = new List<ElementScorerResult>(requested.Count);
        foreach (var scorerName in requested)
        {
            var scorer = document.Scorers.FirstOrDefault(
                candidate => string.Equals(candidate.Scorer, scorerName, StringComparison.Ordinal));
            if (scorer?.Segments is null || scorer.Segments.Count != count)
            {
                _breaker.RecordFailure();
                return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
            }

            var rows = new List<SegmentElementScores>(count);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                var segment = scorer.Segments.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                if (segment?.Elements is null || segment.Elements.Count != ElementVocabulary.All.Count)
                {
                    _breaker.RecordFailure();
                    return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
                }

                var points = new List<ElementScore>(ElementVocabulary.All.Count);
                foreach (var element in ElementVocabulary.All)
                {
                    var point = segment.Elements.FirstOrDefault(
                        candidate => string.Equals(candidate.Element, element, StringComparison.Ordinal));
                    if (point is null)
                    {
                        _breaker.RecordFailure();
                        return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
                    }

                    var mapped = MapPoint(point, scorerName);
                    if (mapped is null)
                    {
                        _breaker.RecordFailure();
                        return ElementFailure(count, requested, ElementNotProducedReason.ContractViolation);
                    }

                    points.Add(mapped);
                }

                rows.Add(new SegmentElementScores(ordinal, points));
            }

            results.Add(new ElementScorerResult(
                scorerName,
                scorer.ScorerVersion ?? ElementScorers.VersionOf(scorerName) ?? "unknown",
                rows));
        }

        _metadata ??= new EmbeddingMetadata(
            document.ModelTag!,
            null,
            document.Dimension,
            768,
            document.PrefixVersion!,
            document.Dtype!,
            "embeddinggemma-2/text-only/v1");
        _breaker.RecordSuccess();
        return new ElementClassification(_metadata, document.VocabularyVersion!, results);
    }

    /// <summary>
    /// Maps one wire point, or null when the point does not satisfy the contract. Null means the
    /// whole response is refused rather than partially believed.
    /// </summary>
    private static ElementScore? MapPoint(ElementPoint point, string scorer)
    {
        if (!ElementVocabulary.Contains(point.Element) || !ElementScorers.Contains(point.Scorer)
            || !string.Equals(point.Scorer, scorer, StringComparison.Ordinal))
        {
            return null;
        }

        if (point.Availability == "available")
        {
            if (point.Score is null || !double.IsFinite(point.Score.Value))
            {
                return null;
            }

            if (string.Equals(scorer, ElementScorers.PrototypeKnn, StringComparison.Ordinal))
            {
                if (point.Score is < -2.0 or > 2.0)
                {
                    return null;
                }

                if (string.IsNullOrEmpty(point.NearestExemplarId))
                {
                    return null;
                }
            }

            if (string.Equals(scorer, ElementScorers.LinearProbe, StringComparison.Ordinal)
                && point.Score is < 0.0 or > 1.0)
            {
                return null;
            }

            return new ElementScore(
                point.Element!, ElementAvailability.Available, point.Score, point.NearestExemplarId,
                ElementNotProducedReason.None);
        }

        if (point.Availability != "not_produced" || point.Score is not null
            || point.NearestExemplarId is not null)
        {
            return null;
        }

        return point.Reason switch
        {
            "no_exemplars" => Absent(point.Element!, ElementNotProducedReason.NoExemplars),
            "probe_not_trained" => Absent(point.Element!, ElementNotProducedReason.ProbeNotTrained),
            "artefact_missing" => Absent(point.Element!, ElementNotProducedReason.ArtefactMissing),
            "unauthorized" => Absent(point.Element!, ElementNotProducedReason.Unauthorized),
            "service_unavailable" => Absent(point.Element!, ElementNotProducedReason.ServiceUnavailable),
            "timed_out" => Absent(point.Element!, ElementNotProducedReason.TimedOut),
            "model_tag_mismatch" => Absent(point.Element!, ElementNotProducedReason.ModelTagMismatch),
            "dimension_mismatch" => Absent(point.Element!, ElementNotProducedReason.DimensionMismatch),
            "prefix_version_mismatch" => Absent(point.Element!, ElementNotProducedReason.PrefixVersionMismatch),
            "unsupported_dtype" => Absent(point.Element!, ElementNotProducedReason.UnsupportedDtype),
            "contract_violation" => Absent(point.Element!, ElementNotProducedReason.ContractViolation),
            "segment_too_long" => Absent(point.Element!, ElementNotProducedReason.SegmentTooLong),
            "too_many_segments" => Absent(point.Element!, ElementNotProducedReason.TooManySegments),
            "empty_segment" => Absent(point.Element!, ElementNotProducedReason.EmptySegment),
            "circuit_open" => Absent(point.Element!, ElementNotProducedReason.CircuitOpen),
            _ => null,
        };
    }

    private static ElementScore Absent(string element, ElementNotProducedReason reason) =>
        new(element, ElementAvailability.NotProduced, null, null, reason);

    private static ElementClassification ElementFailure(
        int count,
        IReadOnlyList<string> scorers,
        ElementNotProducedReason reason)
    {
        var results = new List<ElementScorerResult>(scorers.Count);
        foreach (var scorer in scorers)
        {
            var rows = new List<SegmentElementScores>(count);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                rows.Add(new SegmentElementScores(
                    ordinal,
                    ElementVocabulary.All
                        .Select(element => Absent(element, reason))
                        .ToList()));
            }

            results.Add(new ElementScorerResult(
                scorer, ElementScorers.VersionOf(scorer) ?? "unknown", rows));
        }

        return new ElementClassification(null, ElementVocabulary.Version, results);
    }

    private static ElementNotProducedReason ToElementReason(EmbeddingNotProducedReason reason) => reason switch
    {
        EmbeddingNotProducedReason.Unauthorized => ElementNotProducedReason.Unauthorized,
        EmbeddingNotProducedReason.TokenUnavailable => ElementNotProducedReason.Unauthorized,
        EmbeddingNotProducedReason.ServiceUnavailable => ElementNotProducedReason.ServiceUnavailable,
        EmbeddingNotProducedReason.ServiceUnreachable => ElementNotProducedReason.ServiceUnavailable,
        EmbeddingNotProducedReason.TimedOut => ElementNotProducedReason.TimedOut,
        EmbeddingNotProducedReason.ModelTagMismatch => ElementNotProducedReason.ModelTagMismatch,
        EmbeddingNotProducedReason.DimensionMismatch => ElementNotProducedReason.DimensionMismatch,
        EmbeddingNotProducedReason.PrefixVersionMismatch => ElementNotProducedReason.PrefixVersionMismatch,
        EmbeddingNotProducedReason.UnsupportedDtype => ElementNotProducedReason.UnsupportedDtype,
        EmbeddingNotProducedReason.TooManySegments => ElementNotProducedReason.TooManySegments,
        EmbeddingNotProducedReason.SegmentTooLong => ElementNotProducedReason.SegmentTooLong,
        EmbeddingNotProducedReason.EmptySegment => ElementNotProducedReason.EmptySegment,
        EmbeddingNotProducedReason.CircuitOpen => ElementNotProducedReason.CircuitOpen,
        _ => ElementNotProducedReason.ContractViolation,
    };

    private sealed record ClassifyRequest
    {
        [JsonPropertyName("segments")]
        public required IReadOnlyList<string> Segments { get; init; }

        [JsonPropertyName("scorers")]
        public required IReadOnlyList<string> Scorers { get; init; }
    }

    private sealed record ClassifyResponse
    {
        [JsonPropertyName("model_tag")]
        public string? ModelTag { get; init; }

        [JsonPropertyName("prefix_version")]
        public string? PrefixVersion { get; init; }

        [JsonPropertyName("dimension")]
        public int Dimension { get; init; }

        [JsonPropertyName("dtype")]
        public string? Dtype { get; init; }

        [JsonPropertyName("vocabulary_version")]
        public string? VocabularyVersion { get; init; }

        [JsonPropertyName("scorers")]
        public List<ScorerResponse>? Scorers { get; init; }
    }

    private sealed record ScorerResponse
    {
        [JsonPropertyName("scorer")]
        public string? Scorer { get; init; }

        [JsonPropertyName("scorer_version")]
        public string? ScorerVersion { get; init; }

        [JsonPropertyName("segments")]
        public List<SegmentResponse>? Segments { get; init; }
    }

    private sealed record SegmentResponse
    {
        [JsonPropertyName("ordinal")]
        public int Ordinal { get; init; }

        [JsonPropertyName("elements")]
        public List<ElementPoint>? Elements { get; init; }
    }

    private sealed record ElementPoint
    {
        [JsonPropertyName("element")]
        public string? Element { get; init; }

        [JsonPropertyName("scorer")]
        public string? Scorer { get; init; }

        [JsonPropertyName("availability")]
        public string? Availability { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }

        [JsonPropertyName("nearest_exemplar_id")]
        public string? NearestExemplarId { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
