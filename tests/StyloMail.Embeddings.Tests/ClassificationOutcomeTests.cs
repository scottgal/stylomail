using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Embeddings;

namespace StyloMail.Embeddings.Tests;

/// <summary>
/// Every expected failure is a typed outcome, not an exception.
/// </summary>
/// <remarks>
/// The mapping is asserted one branch at a time because a catch-all is how a closed reason vocabulary
/// stops being closed. A test that only proved "it did not throw" would pass on a catch-all too.
/// </remarks>
public sealed class ClassificationOutcomeTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly string[] OneSegment = { "one segment" };
    private static readonly string[] UnknownScorer = { "magic" };
    private const int Dimension = 256;

    private readonly string _directory;
    private readonly string _tokenPath;

    public ClassificationOutcomeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "embeddings-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _tokenPath = Path.Combine(_directory, "token");
        File.WriteAllText(_tokenPath, Token + "\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test result.
        }
    }

    [Fact]
    public async Task A_missing_token_is_no_analyser_configured_and_makes_no_request()
    {
        var handler = new ScriptedHandler();
        File.Delete(_tokenPath);

        var outcome = await Provider(handler).ClassifyAsync(OneSegment);

        Assert.Equal(
            ClassificationUnavailableReason.NoAnalyserConfigured,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_401_is_unauthorized()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, new { reason = "unauthorized" });

        Assert.Equal(
            ClassificationUnavailableReason.Unauthorized,
            Assert.IsType<ClassificationOutcome.Unavailable>(await Provider(handler).ClassifyAsync(OneSegment)).Reason);
    }

    [Fact]
    public async Task A_persistent_503_is_service_unavailable()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, new { reason = "model_not_loaded" });
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, new { reason = "model_not_loaded" });

        Assert.Equal(
            ClassificationUnavailableReason.ServiceUnavailable,
            Assert.IsType<ClassificationOutcome.Unavailable>(await Provider(handler).ClassifyAsync(OneSegment)).Reason);
    }

    [Fact]
    public async Task A_call_past_its_deadline_is_timed_out()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueMetadata();
        handler.EnqueueHanging();

        var options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(120);

        Assert.Equal(
            ClassificationUnavailableReason.TimedOut,
            Assert.IsType<ClassificationOutcome.Unavailable>(await Provider(handler, options).ClassifyAsync(OneSegment)).Reason);
    }

    [Fact]
    public async Task A_different_model_is_unsupported_model()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, Metadata(modelTag: "other/model"));

        Assert.Equal(
            ClassificationUnavailableReason.UnsupportedModel,
            Assert.IsType<ClassificationOutcome.Unavailable>(await Provider(handler).ClassifyAsync(OneSegment)).Reason);
    }

    [Fact]
    public async Task An_unknown_element_in_the_response_is_a_contract_failure()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueMetadata();
        var body = ClassifyBody();
        body = body.Replace("\"credential_request\"", "\"magic_element\"", StringComparison.Ordinal);
        handler.Enqueue(HttpStatusCode.OK, body, alreadyJson: true);

        Assert.Equal(
            ClassificationUnavailableReason.ContractViolation,
            Assert.IsType<ClassificationOutcome.Unavailable>(await Provider(handler).ClassifyAsync(OneSegment)).Reason);
    }

    [Fact]
    public async Task A_batch_over_the_bound_is_invalid_request_without_a_request()
    {
        var handler = new ScriptedHandler();
        var segments = Enumerable.Range(0, 33).Select(index => $"segment {index}").ToArray();

        var outcome = await Provider(handler).ClassifyAsync(segments);

        Assert.Equal(
            ClassificationUnavailableReason.InvalidRequest,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_empty_batch_is_a_success_with_no_rows_and_no_request()
    {
        var handler = new ScriptedHandler();

        var outcome = await Provider(handler).ClassifyAsync(Array.Empty<string>());

        var success = Assert.IsType<ClassificationOutcome.Success>(outcome);
        Assert.Empty(success.Classification.Scorers.Single().Segments);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_scorer_is_a_programmer_error_and_throws()
    {
        var handler = new ScriptedHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(handler).ClassifyAsync(OneSegment, UnknownScorer));
    }

    private SegmentEmbeddingProvider Provider(ScriptedHandler handler, EmbeddingOptions? options = null) =>
        new(new HttpClient(handler), options ?? Options(), TimeProvider.System);

    private EmbeddingOptions Options() => new()
    {
        TokenFilePath = _tokenPath,
        Dimension = Dimension,
        ModelTag = "google/embeddinggemma-2",
        PrefixVersion = "classification/1",
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static object Metadata(string modelTag = "google/embeddinggemma-2") => new
    {
        model_tag = modelTag,
        model_revision = "stub",
        dimension = Dimension,
        native_dimension = 768,
        prefix = "task: classification | query: ",
        prefix_version = "classification/1",
        dtype = "bfloat16",
        device = "stub",
        normalized = true,
        contract = "embeddinggemma-2/text-only/v1",
    };

    private static string ClassifyBody()
    {
        var elements = ElementVocabulary.All.Select(element => new Dictionary<string, object?>
        {
            ["element"] = element,
            ["scorer"] = ElementScorers.PrototypeKnn,
            ["availability"] = "available",
            ["score"] = 0.1,
            ["nearest_exemplar_id"] = $"seed-1:{element}:0001",
            ["reason"] = null,
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            model_tag = "google/embeddinggemma-2",
            prefix_version = "classification/1",
            dimension = Dimension,
            dtype = "bfloat16",
            vocabulary_version = "elements/1",
            scorers = new[]
            {
                new
                {
                    scorer = ElementScorers.PrototypeKnn,
                    scorer_version = ElementScorers.PrototypeKnnVersion,
                    segments = new[] { new { ordinal = 0, elements } },
                },
            },
        });
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public List<Uri> Requests { get; } = new();

        public void EnqueueMetadata() => Enqueue(HttpStatusCode.OK, Metadata());

        public void Enqueue(HttpStatusCode status, object payload, bool alreadyJson = false)
        {
            var body = alreadyJson ? (string)payload : JsonSerializer.Serialize(payload);
            _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
            Requests.Add(request.RequestUri!);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"unscripted request to {request.RequestUri}");
            }

            return await _responses.Dequeue()(cancellationToken);
        }
    }
}
