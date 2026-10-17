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
    public async Task A_classify_response_captured_from_the_service_is_accepted_end_to_end()
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

        var result = await provider.ClassifyAsync(Segments, Scorers);

        Assert.Equal(ElementVocabulary.Version, result.VocabularyVersion);
        Assert.Equal(2, result.Scorers.Count);

        foreach (var scorer in result.Scorers)
        {
            Assert.Equal(2, scorer.Segments.Count);
            Assert.Equal(5, scorer.Segments[0].Elements.Count);
            Assert.All(scorer.Segments, row => Assert.Equal(
                ElementVocabulary.All, row.Elements.Select(point => point.Element).ToList()));

            if (scorer.Scorer == ElementScorers.PrototypeKnn)
            {
                Assert.All(scorer.Segments.SelectMany(row => row.Elements), point =>
                {
                    Assert.Equal(ElementAvailability.Available, point.Availability);
                    Assert.InRange(point.Score!.Value, -2.0, 2.0);
                    Assert.False(string.IsNullOrEmpty(point.NearestExemplarId));
                });
            }
            else
            {
                Assert.All(scorer.Segments.SelectMany(row => row.Elements), point =>
                {
                    Assert.Equal(ElementAvailability.Available, point.Availability);
                    Assert.InRange(point.Score!.Value, 0.0, 1.0);
                    Assert.Null(point.NearestExemplarId);
                });
            }
        }
    }

    [Fact]
    public async Task A_response_naming_an_unknown_element_is_refused_whole()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var classify = (await File.ReadAllTextAsync(Fixture("service-classify.json")))
            .Replace("\"credential_request\"", "\"magic_element\"", StringComparison.Ordinal);

        var options = new EmbeddingOptions { TokenFilePath = _tokenPath, Dimension = 256 };
        var provider = new SegmentEmbeddingProvider(
            new HttpClient(new CapturedHandler(metadata, classify)), options, TimeProvider.System);

        var result = await provider.ClassifyAsync(Segments, new[] { ElementScorers.PrototypeKnn });

        Assert.All(
            result.Scorers.SelectMany(scorer => scorer.Segments).SelectMany(row => row.Elements),
            point =>
            {
                Assert.Equal(ElementAvailability.NotProduced, point.Availability);
                Assert.Equal(ElementNotProducedReason.ContractViolation, point.Reason);
                Assert.Null(point.Score);
            });
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
