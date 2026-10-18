using System.Net;
using System.Text;
using StyloMail.Embeddings;

namespace StyloMail.Embeddings.Tests;

/// <summary>
/// Replays a classify response captured from the running service through the client.
/// </summary>
/// <remarks>
/// The value of this test is that the bytes were produced by the service, not written by the same
/// author as the parser. Both scorers are present in the capture, so the test also pins the two
/// score meanings: a kNN margin with a nearest exemplar id, and a probe probability without one.
/// </remarks>
public sealed class CapturedClassifyContractTests : IDisposable
{
    private static readonly string[] Segments = { "segment one", "segment two" };
    private static readonly string[] Scorers = { ElementScorers.PrototypeKnn, ElementScorers.LinearProbe };

    private readonly string _directory;
    private readonly string _tokenPath;

    public CapturedClassifyContractTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "embeddings-classify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _tokenPath = Path.Combine(_directory, "token");
        File.WriteAllText(_tokenPath, "0123456789abcdef0123456789abcdef\n");
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
    public async Task A_classify_response_captured_under_elements1_is_refused_under_elements2()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var classify = await File.ReadAllTextAsync(Fixture("service-classify.json"));

        var options = new EmbeddingOptions
        {
            TokenFilePath = _tokenPath,
            Dimension = 256,
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
        };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var outcome = await provider.ClassifyAsync(Segments, Scorers);

        // The capture is from the running elements/1 service with five elements. Under elements/2 it
        // is a version mismatch, and refusing is the contract working, not the fixture rotting.
        Assert.Equal(
            ClassificationUnavailableReason.ContractViolation,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
    }

    [Fact]
    public async Task An_elements2_response_with_three_elements_is_accepted()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var elements = string.Join(",", ElementVocabulary.All.Select(element =>
            $"{{\"element\":\"{element}\",\"scorer\":\"{ElementScorers.PrototypeKnn}\",\"availability\":\"available\",\"score\":0.1,\"nearest_exemplar_id\":\"ex:{element}:0001\",\"reason\":null}}"));
        var classify = "{\"model_tag\":\"google/embeddinggemma-2\",\"prefix_version\":\"classification/1\"," +
            "\"dimension\":256,\"dtype\":\"bfloat16\",\"vocabulary_version\":\"elements/2\"," +
            "\"scorers\":[{\"scorer\":\"prototype_knn\",\"scorer_version\":\"prototype_knn/corpus-1\"," +
            "\"segments\":[{\"ordinal\":0,\"elements\":[" + elements + "]}]}]}";

        var options = new EmbeddingOptions { TokenFilePath = _tokenPath, Dimension = 256 };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var outcome = await provider.ClassifyAsync(Segments.Take(1).ToArray(), new[] { ElementScorers.PrototypeKnn });
        var success = Assert.IsType<ClassificationOutcome.Success>(outcome);

        Assert.Equal(ElementVocabulary.Version, success.Classification.VocabularyVersion);
        Assert.Equal(ElementVocabulary.All.Count, success.Classification.Scorers[0].Segments[0].Elements.Count);
    }

    [Fact]
    public async Task A_response_naming_an_unknown_element_is_a_contract_unavailable()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var classify = (await File.ReadAllTextAsync(Fixture("service-classify.json")))
            .Replace("\"credential_request\"", "\"magic_element\"", StringComparison.Ordinal);

        var options = new EmbeddingOptions { TokenFilePath = _tokenPath, Dimension = 256 };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var outcome = await provider.ClassifyAsync(Segments, new[] { ElementScorers.PrototypeKnn });
        Assert.Equal(
            ClassificationUnavailableReason.ContractViolation,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
    }

    [Fact]
    public async Task A_missing_scorer_version_is_refused_rather_than_defaulted()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var classify = (await File.ReadAllTextAsync(Fixture("service-classify.json")))
            .Replace("\"scorer_version\"", "\"scorer_version_removed\"", StringComparison.Ordinal);

        var options = new EmbeddingOptions { TokenFilePath = _tokenPath, Dimension = 256 };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var outcome = await provider.ClassifyAsync(Segments, new[] { ElementScorers.PrototypeKnn });

        Assert.Equal(
            ClassificationUnavailableReason.ContractViolation,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
    }

    [Fact]
    public async Task A_scorer_version_that_is_not_the_served_one_is_refused()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var classify = (await File.ReadAllTextAsync(Fixture("service-classify.json")))
            .Replace(ElementScorers.PrototypeKnnVersion, "prototype_knn/seed-1", StringComparison.Ordinal);

        var options = new EmbeddingOptions { TokenFilePath = _tokenPath, Dimension = 256 };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var outcome = await provider.ClassifyAsync(Segments, new[] { ElementScorers.PrototypeKnn });

        Assert.Equal(
            ClassificationUnavailableReason.ContractViolation,
            Assert.IsType<ClassificationOutcome.Unavailable>(outcome).Reason);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private sealed class CapturedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;

        public CapturedHandler(string metadata, string classify) =>
            _bodies = new Queue<string>(new[] { metadata, classify });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_bodies.Count == 0)
            {
                throw new InvalidOperationException($"unscripted request to {request.RequestUri}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
