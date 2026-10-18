using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StyloMail.Embeddings;

namespace StyloMail.Embeddings.Tests;

/// <summary>
/// The provider's contract, exercised against a stub transport.
/// </summary>
/// <remarks>
/// The property under test throughout is the same one: a fault must produce absence with a name, and
/// must never produce a vector a caller could store. The negative cases therefore assert both the
/// reason and the absence of a vector, because a reason with a vector attached would be the defect
/// wearing the right label.
/// </remarks>
public sealed class SegmentEmbeddingProviderTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string ModelTag = "google/embeddinggemma-2";
    private const string PrefixVersion = "classification/1";
    private const int Dimension = 256;

    private static readonly string[] OneSegment = { "one segment" };
    private static readonly string[] TwoSegments = { "first segment", "second segment" };
    private static readonly string[] FirstWord = { "one" };
    private static readonly string[] SecondWord = { "two" };

    private readonly string _tokenDirectory;
    private readonly string _tokenPath;

    public SegmentEmbeddingProviderTests()
    {
        _tokenDirectory = Path.Combine(Path.GetTempPath(), "embeddings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tokenDirectory);
        _tokenPath = Path.Combine(_tokenDirectory, "token");
        File.WriteAllText(_tokenPath, Token + "\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tokenDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test result.
        }
    }

    [Fact]
    public async Task A_matching_service_produces_one_unit_vector_per_segment()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(StandardVectors(2));

        var batch = await Provider(handler).EmbedAsync(TwoSegments);

        Assert.True(batch.AllProduced);
        Assert.Equal(2, batch.Segments.Count);
        Assert.All(batch.Segments, row => Assert.Equal(Dimension, row.Vector!.Length));
        Assert.All(batch.Segments, row => Assert.Equal(SegmentEmbeddingAvailability.Available, row.Availability));
        Assert.Equal(ModelTag, batch.Metadata!.ModelTag);
        Assert.Equal(PrefixVersion, batch.Metadata.PrefixVersion);
    }

    [Fact]
    public async Task The_bearer_token_is_sent_and_never_appears_in_the_metadata_request()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(StandardVectors(1));

        await Provider(handler).EmbedAsync(OneSegment);

        Assert.All(
            handler.Requests,
            request => Assert.Equal("Bearer " + Token, request.Authorization));
    }

    [Fact]
    public async Task A_model_tag_mismatch_produces_absence_and_no_encode_call()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Metadata(modelTag: "google/other-model"));

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.ModelTagMismatch, row.Reason));
        Assert.All(batch.Segments, row => Assert.Null(row.Vector));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_dimension_mismatch_produces_absence()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Metadata(dimension: 768));

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.DimensionMismatch, row.Reason));
    }

    [Fact]
    public async Task A_prefix_version_mismatch_produces_absence()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Metadata(prefixVersion: "sentence-similarity/1"));

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(
            batch.Segments,
            row => Assert.Equal(EmbeddingNotProducedReason.PrefixVersionMismatch, row.Reason));
    }

    [Fact]
    public async Task Float16_is_refused_because_the_card_says_it_silently_degrades()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(HttpStatusCode.OK, Metadata(dtype: "float16"));

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.UnsupportedDtype, row.Reason));
    }

    [Fact]
    public async Task A_vector_that_is_not_unit_length_is_refused()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(new[] { ScaledVector(Dimension, 3.0f) });

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(
            batch.Segments,
            row => Assert.Equal(EmbeddingNotProducedReason.VectorNotNormalised, row.Reason));
    }

    [Fact]
    public async Task A_non_finite_component_is_refused()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        var vector = UnitVector(Dimension);
        vector[7] = float.NaN;
        handler.EnqueueVectors(new[] { vector });

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.NonFiniteVector, row.Reason));
    }

    [Fact]
    public async Task A_vector_of_the_wrong_width_is_refused()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(new[] { UnitVector(128) });

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.DimensionMismatch, row.Reason));
    }

    [Fact]
    public async Task A_missing_token_file_means_no_request_is_made_at_all()
    {
        var handler = new StubHandler();
        File.Delete(_tokenPath);

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.TokenUnavailable, row.Reason));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unauthorised_response_produces_absence()
    {
        var handler = new StubHandler();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, new { reason = "unauthorized" });

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.Unauthorized, row.Reason));
    }

    [Fact]
    public async Task A_single_503_is_retried_and_then_succeeds()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, new { reason = "model_not_loaded" });
        handler.EnqueueVectors(StandardVectors(1));

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.True(batch.AllProduced);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_second_503_gives_up_rather_than_retrying_again()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, new { reason = "model_not_loaded" });
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, new { reason = "model_not_loaded" });

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(
            batch.Segments,
            row => Assert.Equal(EmbeddingNotProducedReason.ServiceUnavailable, row.Reason));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_call_that_exceeds_the_deadline_is_reported_as_timed_out()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata(delay: TimeSpan.Zero);
        handler.EnqueueHanging();

        var options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(150);
        var batch = await Provider(handler, options).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.TimedOut, row.Reason));
    }

    [Fact]
    public async Task A_server_that_sends_headers_then_stalls_is_timed_out_rather_than_hung()
    {
        // The control for review finding 5: with ResponseHeadersRead, headers completing does not
        // mean the exchange completed. A body that never arrives must be cut by the same deadline.
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueStallingBody();

        var options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(200);

        var started = TimeProvider.System.GetTimestamp();
        var batch = await Provider(handler, options).EmbedAsync(OneSegment);
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.TimedOut, row.Reason));
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"the call took {elapsed}, so the deadline did not cover the body");
    }

    [Fact]
    public async Task A_declared_body_larger_than_the_cap_is_refused_before_reading()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueOversizedBody();

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.ContractViolation, row.Reason));
    }

    [Fact]
    public async Task A_refusing_service_opens_the_circuit_and_the_next_call_makes_no_request()
    {
        var handler = new StubHandler();
        for (var i = 0; i < 6; i++)
        {
            handler.EnqueueJson(HttpStatusCode.InternalServerError, new { reason = "boom" });
        }

        var provider = Provider(handler);
        for (var i = 0; i < 5; i++)
        {
            await provider.EmbedAsync(OneSegment);
        }

        var requestsBefore = handler.Requests.Count;
        var batch = await provider.EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.CircuitOpen, row.Reason));
        Assert.Equal(requestsBefore, handler.Requests.Count);
    }

    [Fact]
    public async Task Too_many_segments_is_refused_without_a_request()
    {
        var handler = new StubHandler();
        var segments = Enumerable.Range(0, 33).Select(i => $"segment {i}").ToArray();

        var batch = await Provider(handler).EmbedAsync(segments);

        Assert.Equal(33, batch.Segments.Count);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.TooManySegments, row.Reason));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_segment_over_the_character_bound_is_refused_without_a_request()
    {
        var handler = new StubHandler();

        var batch = await Provider(handler).EmbedAsync(new[] { new string('x', 6001) });

        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.SegmentTooLong, row.Reason));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_empty_batch_returns_no_rows_and_makes_no_request()
    {
        var handler = new StubHandler();

        var batch = await Provider(handler).EmbedAsync(Array.Empty<string>());

        Assert.Empty(batch.Segments);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Metadata_is_not_refetched_on_every_call()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(StandardVectors(1));
        handler.EnqueueVectors(StandardVectors(1));

        var provider = Provider(handler);
        await provider.EmbedAsync(FirstWord);
        await provider.EmbedAsync(SecondWord);

        Assert.Equal(1, handler.Requests.Count(r => r.Path.EndsWith("/metadata", StringComparison.Ordinal)));
        Assert.Equal(2, handler.Requests.Count(r => r.Path.EndsWith("/embed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_response_naming_another_model_than_the_metadata_did_is_refused()
    {
        var handler = new StubHandler();
        handler.EnqueueMetadata();
        handler.EnqueueVectors(StandardVectors(1), modelTag: "google/other-model");

        var batch = await Provider(handler).EmbedAsync(OneSegment);

        Assert.False(batch.AnyProduced);
        Assert.All(batch.Segments, row => Assert.Equal(EmbeddingNotProducedReason.ModelTagMismatch, row.Reason));
    }

    private SegmentEmbeddingProvider Provider(StubHandler handler, EmbeddingOptions? options = null) =>
        new(new HttpClient(handler), options ?? Options(), TimeProvider.System);

    private EmbeddingOptions Options() => new()
    {
        TokenFilePath = _tokenPath,
        ModelTag = ModelTag,
        Dimension = Dimension,
        PrefixVersion = PrefixVersion,
        MaxSegments = 32,
        MaxSegmentCharacters = 6000,
        Timeout = TimeSpan.FromSeconds(5),
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };

    private static float[][] StandardVectors(int count) =>
        Enumerable.Range(0, count).Select(_ => UnitVector(Dimension)).ToArray();

    private static float[] UnitVector(int dimension)
    {
        var vector = new float[dimension];
        var value = (float)(1.0 / Math.Sqrt(dimension));
        Array.Fill(vector, value);
        return vector;
    }

    private static float[] ScaledVector(int dimension, float scale)
    {
        var vector = UnitVector(dimension);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }

        return vector;
    }

    private static object Metadata(
        string modelTag = ModelTag,
        int dimension = Dimension,
        string prefixVersion = PrefixVersion,
        string dtype = "bfloat16") => new
    {
        model_tag = modelTag,
        model_revision = "stub",
        dimension,
        native_dimension = 768,
        prefix = "task: classification | query: ",
        prefix_version = prefixVersion,
        dtype,
        device = "stub",
        normalized = true,
        contract = "embeddinggemma-2/text-only/v1",
    };

    private static readonly JsonSerializerOptions StubJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public List<RecordedRequest> Requests { get; } = new();

        public void EnqueueMetadata(TimeSpan? delay = null) =>
            EnqueueJson(HttpStatusCode.OK, Metadata(), delay);

        public void EnqueueVectors(float[][] vectors, string modelTag = ModelTag) =>
            EnqueueJson(
                HttpStatusCode.OK,
                new
                {
                    vectors,
                    dimension = vectors.FirstOrDefault()?.Length ?? Dimension,
                    model_tag = modelTag,
                    prefix_version = PrefixVersion,
                    dtype = "bfloat16",
                    normalized = true,
                    truncated = vectors.Select(_ => false).ToArray(),
                    encode_ms = 1.0,
                });

        public void EnqueueJson(HttpStatusCode status, object payload, TimeSpan? delay = null)
        {
            var body = JsonSerializer.Serialize(payload, StubJson);
            _responses.Enqueue(async cancellationToken =>
            {
                if (delay is { } wait)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            });
        }

        public void EnqueueStallingBody() =>
            _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingContent(),
            }));

        public void EnqueueOversizedBody()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            content.Headers.ContentLength = 16 * 1024 * 1024;
            _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            }));
        }

        public void EnqueueHanging() =>
            _responses.Enqueue(async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"the provider made an unscripted request to {request.RequestUri}");
            }

            return await _responses.Dequeue()(cancellationToken);
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.Delay(TimeSpan.FromMinutes(5));

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return 0;
        }
    }

    private sealed record RecordedRequest(string Path, string Authorization);
}
