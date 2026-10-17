using System.Net;
using System.Text;
using System.Text.Json;
using StyloMail.Embeddings;

namespace StyloMail.Embeddings.Tests;

/// <summary>
/// Replays bytes the real service returned on the inference host through the provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than more stub tests.</b> A stub that returns the shape the author
/// believed the service sends proves only that the author can write the same mistake twice. These
/// two documents were captured from the running service with
/// <c>tools/embeddings/service/capture-contract.sh</c>: 256 float values in the embed document, the
/// metadata document the provider validates first. If the two sides drift, this test fails on a
/// real captured body rather than on a fixture nobody re-derives.
/// </para>
/// <para>
/// The capture contains no message text, only derived values. It is committed on purpose: a test
/// that reads from a path outside the repo cannot run on a machine that has never seen the host.
/// </para>
/// </remarks>
public sealed class CapturedServiceContractTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static readonly string[] TwoSegments = { "segment one", "segment two" };

    private readonly string _directory;
    private readonly string _tokenPath;

    public CapturedServiceContractTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "embeddings-capture-" + Guid.NewGuid().ToString("N"));
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
            // Leaving a temp directory behind is not a test result.
        }
    }

    [Fact]
    public async Task A_response_captured_from_the_running_service_is_accepted_end_to_end()
    {
        var metadata = await File.ReadAllTextAsync(Fixture("service-metadata.json"));
        var embed = await File.ReadAllTextAsync(Fixture("service-embed.json"));

        var handler = new CapturedHandler(metadata, embed);
        var options = new EmbeddingOptions
        {
            TokenFilePath = _tokenPath,
            Dimension = 256,
            ModelTag = "google/embeddinggemma-2",
            PrefixVersion = "classification/1",
        };
        var provider = new SegmentEmbeddingProvider(new HttpClient(handler), options, TimeProvider.System);

        var batch = await provider.EmbedAsync(TwoSegments);

        Assert.True(batch.AllProduced);
        Assert.Equal(2, batch.Segments.Count);
        Assert.All(batch.Segments, row => Assert.Equal(256, row.Vector!.Length));
        Assert.All(
            batch.Segments,
            row =>
            {
                var norm = Math.Sqrt(row.Vector!.Sum(value => (double)value * value));
                Assert.InRange(norm, 1.0 - 1e-5, 1.0 + 1e-5);
            });
        Assert.Equal("bfloat16", batch.Metadata!.Dtype);
        Assert.Equal("classification/1", batch.Metadata.PrefixVersion);
        Assert.Equal(768, batch.Metadata.NativeDimension);
    }

    [Fact]
    public void The_captured_metadata_names_a_dtype_that_is_not_float16()
    {
        // Read the field rather than searching for the substring: "bfloat16" contains "float16",
        // so a DoesNotContain over the raw document is a negative assertion that can never hold.
        using var document = JsonDocument.Parse(File.ReadAllText(Fixture("service-metadata.json")));
        var dtype = document.RootElement.GetProperty("dtype").GetString();
        Assert.Equal("bfloat16", dtype);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private sealed class CapturedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;

        public CapturedHandler(string metadata, string embed) =>
            _bodies = new Queue<string>(new[] { metadata, embed });

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
