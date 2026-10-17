using StyloMail.AccessProxy.Hosting;
using StyloMail.Embeddings;

namespace StyloMail.Host.Proxy;

/// <summary>One stored element point, as the route projects it.</summary>
public sealed record ProxyElementRow
{
    public required string MessageToken { get; init; }

    public required string ThreadToken { get; init; }

    /// <summary><c>Retrieval</c> or <c>Submission</c>.</summary>
    public required string TrafficKind { get; init; }

    public required int Ordinal { get; init; }

    public required string Element { get; init; }

    public required string Scorer { get; init; }

    public string? ScorerVersion { get; init; }

    public string? VocabularyVersion { get; init; }

    public string? ModelTag { get; init; }

    public string? PrefixVersion { get; init; }

    public int? Dimension { get; init; }

    public double? Score { get; init; }

    public string? NearestExemplarId { get; init; }

    /// <summary><c>available</c> or <c>not_produced</c>.</summary>
    public required string Availability { get; init; }

    /// <summary>A wire reason name; <c>none</c> on an available point.</summary>
    public required string AvailabilityReason { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Reads the element series for one thread.</summary>
public interface IProxyElementReader
{
    ValueTask<IReadOnlyList<ProxyElementRow>> ListElementPointsAsync(
        string tenantId,
        string threadToken,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <summary>
/// Composes the loopback classifier only when a deployment explicitly enables it.
/// </summary>
/// <remarks>
/// The Host never starts the embedding service or a tunnel; it only creates an HTTP client. A
/// non-loopback endpoint is rejected at startup because the whole point of this path is that
/// message segments never leave the machine.
/// </remarks>
internal static class ProxyElementComposition
{
    public static ProxyElementConfiguration Create(IServiceProvider services, bool enabled)
    {
        if (!enabled)
        {
            return new ProxyElementConfiguration(null, enabled: false);
        }

        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmbeddingOptions>>().Value;
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
        {
            throw new ProxyConfigurationException(
                "Element analysis is enabled but its embedding endpoint is not a loopback address.");
        }

        var http = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = options.Timeout,
        };

        var classifier = new SegmentEmbeddingProvider(
            http,
            options,
            services.GetRequiredService<TimeProvider>(),
            services.GetService<Microsoft.Extensions.Logging.ILogger<SegmentEmbeddingProvider>>());

        // A missing token file is not a composition failure: the provider reports token_unavailable
        // per point, which is the explicit absence state. Only an invalid endpoint is fatal.
        return new ProxyElementConfiguration(classifier, enabled: true);
    }
}

/// <summary>Whether this deployment composes element analysis, and the classifier when it does.</summary>
/// <remarks>
/// Analysis is off unless explicitly enabled with a loopback endpoint and a readable token file.
/// A missing token is not "no analyser configured"; it is a deployment that intended analysis and
/// cannot do it, so the classifier is still composed and returns token_unavailable, while this flag
/// reports the configuration state without exposing the address or token.
/// </remarks>
public sealed class ProxyElementConfiguration
{
    public ProxyElementConfiguration(IElementClassifier? classifier, bool enabled)
    {
        Classifier = classifier;
        Enabled = enabled;
    }

    /// <summary>True when the deployment explicitly enabled element analysis.</summary>
    public bool Enabled { get; }

    /// <summary>The composed classifier, or null when analysis is not enabled.</summary>
    public IElementClassifier? Classifier { get; }
}
