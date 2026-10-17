using StyloMail.AccessProxy.Hosting;
using StyloMail.Embeddings;

namespace StyloMail.Host.Proxy;

/// <summary>One stored element point, as the route projects it.</summary>
public sealed record ProxyElementRow
{
    public required string MessageToken { get; init; }

    public required string ThreadToken { get; init; }

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
