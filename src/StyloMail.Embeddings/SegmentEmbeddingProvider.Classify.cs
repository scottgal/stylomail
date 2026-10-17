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
    public async Task<ClassificationOutcome> ClassifyAsync(
        IReadOnlyList<string> segments,
        IReadOnlyList<string>? scorers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var requested = NormaliseScorers(scorers);
        if (segments.Count == 0)
        {
            return new ClassificationOutcome.Success(new ElementClassification(
                _metadata,
                ElementVocabulary.Version,
                requested.Select(scorer => new ElementScorerResult(
                    scorer, ElementScorers.VersionOf(scorer)!, Array.Empty<SegmentElementScores>())).ToList()));
        }

        if (GuardBatch(segments) is { } guardReason)
        {
            return Unavailable(ClassificationUnavailableReason.InvalidRequest);
        }

        if (_breaker.IsOpen)
        {
            return Unavailable(ClassificationUnavailableReason.ServiceUnavailable);
        }

        var (token, tokenReason) = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Unavailable(ClassificationUnavailableReason.NoAnalyserConfigured);
        }

        if (await EnsureMetadataAsync(force: false, cancellationToken).ConfigureAwait(false) is null)
        {
            return Unavailable(ToUnavailableReason(_lastFailure));
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
                return Unavailable(ToUnavailableReason(fault.Reason));
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

    private async Task<ClassificationOutcome> InterpretClassifyAsync(
        HttpResponseMessage response,
        int count,
        IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Unavailable(ClassificationUnavailableReason.Unauthorized);
        }

        if (response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
        {
            return Unavailable(ClassificationUnavailableReason.ContractViolation);
        }

        if (!response.IsSuccessStatusCode)
        {
            _breaker.RecordFailure();
            return Unavailable(ClassificationUnavailableReason.ServiceUnavailable);
        }

        var document = await ReadDocumentAsync<ClassifyResponse>(response, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            _breaker.RecordFailure();
            return Unavailable(ClassificationUnavailableReason.ContractViolation);
        }

        if (!string.Equals(document.ModelTag, _options.ModelTag, StringComparison.Ordinal))
        {
            return Unavailable(ClassificationUnavailableReason.UnsupportedModel);
        }

        if (!string.Equals(document.PrefixVersion, _options.PrefixVersion, StringComparison.Ordinal))
        {
            return Unavailable(ClassificationUnavailableReason.UnsupportedModel);
        }

        if (!IsAcceptedDtype(document.Dtype))
        {
            return Unavailable(ClassificationUnavailableReason.UnsupportedModel);
        }

        if (document.Dimension != _options.Dimension)
        {
            return Unavailable(ClassificationUnavailableReason.UnsupportedModel);
        }

        if (!string.Equals(document.VocabularyVersion, ElementVocabulary.Version, StringComparison.Ordinal)
            || document.Scorers is null)
        {
            _breaker.RecordFailure();
            return Unavailable(ClassificationUnavailableReason.ContractViolation);
        }

        var results = new List<ElementScorerResult>(requested.Count);
        foreach (var scorerName in requested)
        {
            var scorer = document.Scorers.FirstOrDefault(
                candidate => string.Equals(candidate.Scorer, scorerName, StringComparison.Ordinal));
            if (scorer?.Segments is null || scorer.Segments.Count != count)
            {
                _breaker.RecordFailure();
                return Unavailable(ClassificationUnavailableReason.ContractViolation);
            }

            var rows = new List<SegmentElementScores>(count);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                var segment = scorer.Segments.FirstOrDefault(candidate => candidate.Ordinal == ordinal);
                if (segment?.Elements is null || segment.Elements.Count != ElementVocabulary.All.Count)
                {
                    _breaker.RecordFailure();
                    return Unavailable(ClassificationUnavailableReason.ContractViolation);
                }

                var points = new List<ElementScore>(ElementVocabulary.All.Count);
                foreach (var element in ElementVocabulary.All)
                {
                    var point = segment.Elements.FirstOrDefault(
                        candidate => string.Equals(candidate.Element, element, StringComparison.Ordinal));
                    if (point is null)
                    {
                        _breaker.RecordFailure();
                        return Unavailable(ClassificationUnavailableReason.ContractViolation);
                    }

                    var mapped = MapPoint(point, scorerName);
                    if (mapped is null)
                    {
                        _breaker.RecordFailure();
                        return Unavailable(ClassificationUnavailableReason.ContractViolation);
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
        return new ClassificationOutcome.Success(
            new ElementClassification(_metadata, document.VocabularyVersion!, results));
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

    private static ClassificationOutcome Unavailable(ClassificationUnavailableReason reason) =>
        new ClassificationOutcome.Unavailable(reason);

    /// <summary>
    /// Maps the embedding layer's reasons onto the classification outcome's closed set. There is no
    /// default arm that invents a reason: an unmapped value is a mapping defect and throws, because
    /// a silent catch-all is how a closed vocabulary stops being one.
    /// </summary>
    private static ClassificationUnavailableReason ToUnavailableReason(EmbeddingNotProducedReason reason) => reason switch
    {
        EmbeddingNotProducedReason.Unauthorized => ClassificationUnavailableReason.Unauthorized,
        EmbeddingNotProducedReason.TokenUnavailable => ClassificationUnavailableReason.NoAnalyserConfigured,
        EmbeddingNotProducedReason.ServiceUnavailable => ClassificationUnavailableReason.ServiceUnavailable,
        EmbeddingNotProducedReason.ServiceUnreachable => ClassificationUnavailableReason.ServiceUnavailable,
        EmbeddingNotProducedReason.MetadataUnavailable => ClassificationUnavailableReason.ServiceUnavailable,
        EmbeddingNotProducedReason.CircuitOpen => ClassificationUnavailableReason.ServiceUnavailable,
        EmbeddingNotProducedReason.TimedOut => ClassificationUnavailableReason.TimedOut,
        EmbeddingNotProducedReason.ModelTagMismatch => ClassificationUnavailableReason.UnsupportedModel,
        EmbeddingNotProducedReason.DimensionMismatch => ClassificationUnavailableReason.UnsupportedModel,
        EmbeddingNotProducedReason.PrefixVersionMismatch => ClassificationUnavailableReason.UnsupportedModel,
        EmbeddingNotProducedReason.UnsupportedDtype => ClassificationUnavailableReason.UnsupportedModel,
        EmbeddingNotProducedReason.TooManySegments => ClassificationUnavailableReason.InvalidRequest,
        EmbeddingNotProducedReason.SegmentTooLong => ClassificationUnavailableReason.InvalidRequest,
        EmbeddingNotProducedReason.EmptySegment => ClassificationUnavailableReason.InvalidRequest,
        EmbeddingNotProducedReason.ContractViolation => ClassificationUnavailableReason.ContractViolation,
        EmbeddingNotProducedReason.NonFiniteVector => ClassificationUnavailableReason.ContractViolation,
        EmbeddingNotProducedReason.VectorNotNormalised => ClassificationUnavailableReason.ContractViolation,
        EmbeddingNotProducedReason.RequestRejected => ClassificationUnavailableReason.ContractViolation,
        EmbeddingNotProducedReason.None => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "a success has no unavailable reason"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "an unmapped embedding reason cannot become a classification reason"),
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
