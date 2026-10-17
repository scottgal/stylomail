using System.Net;
using System.Net.Http.Headers;
using StyloMail.Embeddings;
using Xunit.Abstractions;

namespace StyloMail.Embeddings.Tests;

/// <summary>
/// A fact that talks to the live embedding service through the SSH tunnel and is skipped when the
/// tunnel is not up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt in, then self checking.</b> The suite must stay runnable on a machine that has never seen
/// the inference host, so this fact requires <c>STYLOMAIL_EMBEDDINGS_LIVE=1</c>. With that set it
/// still skips, naming why, when the token file is absent or nothing answers the health probe.
/// </para>
/// <para>
/// The token is read from a file named by <c>STYLOMAIL_EMBEDDINGS_TOKEN_FILE</c>. It is never a
/// value in this file, in an environment variable, or in a skip message.
/// </para>
/// </remarks>
public sealed class EmbeddingLiveFactAttribute : FactAttribute
{
    public const string GateVariable = "STYLOMAIL_EMBEDDINGS_LIVE";
    public const string TokenVariable = "STYLOMAIL_EMBEDDINGS_TOKEN_FILE";
    public const string EndpointVariable = "STYLOMAIL_EMBEDDINGS_ENDPOINT";

    public EmbeddingLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(GateVariable) != "1")
        {
            Skip = $"Not attempted. Set {GateVariable}=1 with the tunnel up (tools/embeddings/tunnel.sh up) "
                + "to run the live embedding fact.";
            return;
        }

        var tokenFile = TokenFile();
        if (tokenFile is null || !File.Exists(tokenFile))
        {
            Skip = $"Skipped: no token file. Set {TokenVariable} to the path of the service token "
                + "file (mode 0600). The value itself is never read from the environment.";
            return;
        }

        if (!IsHealthy(Endpoint()))
        {
            Skip = $"Skipped: nothing answered /health at {Endpoint()}. Start the tunnel with "
                + "tools/embeddings/tunnel.sh up.";
        }
    }

    internal static string Endpoint() =>
        Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } configured
            ? configured
            : new EmbeddingOptions().Endpoint;

    internal static string? TokenFile() =>
        Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } configured
            ? configured
            : new EmbeddingOptions().TokenFilePath;

    private static bool IsHealthy(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = client.GetAsync(new Uri(uri, "health")).GetAwaiter().GetResult();
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// The live path, end to end through the shipped provider and the SSH tunnel.
/// </summary>
public sealed class LiveEmbeddingServiceTests
{
    private static readonly string[] Segments =
    {
        "Your account will be suspended unless you confirm your password within 24 hours.",
        "The invoice for the October order is attached for your records.",
    };

    private readonly ITestOutputHelper _output;

    public LiveEmbeddingServiceTests(ITestOutputHelper output) => _output = output;

    [EmbeddingLiveFact]
    public async Task The_live_service_produces_comparable_vectors_and_refuses_a_wrong_token()
    {
        var options = new EmbeddingOptions
        {
            Endpoint = EmbeddingLiveFactAttribute.Endpoint(),
            TokenFilePath = EmbeddingLiveFactAttribute.TokenFile()!,
        };
        var provider = new SegmentEmbeddingProvider(new HttpClient(), options, TimeProvider.System);

        var metadata = await provider.GetMetadataAsync();
        Assert.NotNull(metadata);
        Assert.Equal(options.ModelTag, metadata!.ModelTag);
        Assert.Equal(options.Dimension, metadata.Dimension);
        Assert.Equal(options.PrefixVersion, metadata.PrefixVersion);
        Assert.Equal("bfloat16", metadata.Dtype);

        var batch = await provider.EmbedAsync(Segments);
        Assert.True(batch.AllProduced);
        Assert.Equal(2, batch.Segments.Count);
        foreach (var row in batch.Segments)
        {
            Assert.Equal(options.Dimension, row.Vector!.Length);
            var norm = Math.Sqrt(row.Vector!.Sum(value => (double)value * value));
            Assert.InRange(norm, 1.0 - 1e-6, 1.0 + 1e-6);
            Assert.All(row.Vector!, value => Assert.True(float.IsFinite(value)));
        }

        _output.WriteLine(
            "live metadata: tag={0} dimension={1} prefix={2} dtype={3} revision={4}",
            metadata.ModelTag, metadata.Dimension, metadata.PrefixVersion, metadata.Dtype,
            metadata.ModelRevision ?? "null");

        var outcome = await provider.ClassifyAsync(
            Segments, new[] { ElementScorers.PrototypeKnn, ElementScorers.LinearProbe });
        var classified = Assert.IsType<ClassificationOutcome.Success>(outcome).Classification;
        Assert.Equal(ElementVocabulary.Version, classified.VocabularyVersion);
        Assert.Equal(2, classified.Scorers.Count);
        foreach (var scorer in classified.Scorers)
        {
            Assert.Equal(2, scorer.Segments.Count);
            Assert.All(scorer.Segments, row => Assert.Equal(5, row.Elements.Count));
            foreach (var point in scorer.Segments.SelectMany(row => row.Elements))
            {
                Assert.Equal(ElementAvailability.Available, point.Availability);
                Assert.NotNull(point.Score);
                if (scorer.Scorer == ElementScorers.PrototypeKnn)
                {
                    Assert.InRange(point.Score!.Value, -2.0, 2.0);
                    Assert.False(string.IsNullOrEmpty(point.NearestExemplarId));
                }
                else
                {
                    Assert.InRange(point.Score!.Value, 0.0, 1.0);
                    Assert.Null(point.NearestExemplarId);
                }
            }
        }

        _output.WriteLine(
            "live classify: vocabulary={0} scorers={1}",
            classified.VocabularyVersion,
            string.Join(",", classified.Scorers.Select(scorer => scorer.ScorerVersion)));

        // The refused call, asserted at the HTTP status rather than through the provider's mapping,
        // so the 401 is observed and not inferred from a reason.
        using var client = new HttpClient { BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', 32));
        using var response = await client.GetAsync("metadata");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And through the provider: a bad token is absence with a reason, not an exception and not
        // a vector.
        var wrongOptions = new EmbeddingOptions
        {
            Endpoint = options.Endpoint,
            TokenFilePath = WriteWrongToken(),
            ModelTag = options.ModelTag,
            Dimension = options.Dimension,
            PrefixVersion = options.PrefixVersion,
        };
        var wrongProvider = new SegmentEmbeddingProvider(new HttpClient(), wrongOptions, TimeProvider.System);
        var refused = await wrongProvider.EmbedAsync(Segments);
        Assert.False(refused.AnyProduced);
        Assert.All(
            refused.Segments,
            row => Assert.Equal(EmbeddingNotProducedReason.Unauthorized, row.Reason));
        Assert.All(refused.Segments, row => Assert.Null(row.Vector));
    }

    private static string WriteWrongToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "embeddings-wrong-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, new string('x', 32));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }
}
