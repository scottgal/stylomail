using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>Configuration for image inspection and retrieval propagation. Default off.</summary>
public sealed class HostImageOptions
{
    public const string SectionName = "StyloMail:Images";

    /// <summary>Whether image inspection is enabled for new observation traffic.</summary>
    public bool Enabled { get; set; }
}

public enum ProxyImageDisposition
{
    Inspected,
    Refused,
    NotProduced,
}

public enum ProxyImageAvailability
{
    Available,
    ReducedCoverage,
    Unavailable,
    NotApplicable,
}

public enum ProxyImageReason
{
    None,
    EmptyContent,
    NotAnImage,
    UnsupportedFormat,
    FormatMismatch,
    TruncatedHeader,
    HeaderBudgetExceeded,
    InvalidDimensions,
    InvalidStructure,
    InsufficientDecodedLength,
    InspectionBudgetExhausted,
    ImagesNotProduced,
    ServiceUnavailable,
    StorageError,
    CapacityExceeded,
}

public enum ProxyImageFindingKind
{
    TypeMagicMismatch,
    TrailingData,
    EmbeddedArchive,
    EmbeddedExecutable,
    ScriptableFormat,
    ZeroDimensions,
    DimensionLimitExceeded,
    PixelLimitExceeded,
}

public sealed record ProxyImageFinding
{
    public required ProxyImageFindingKind Kind { get; init; }

    public long? Offset { get; init; }

    public string? Detail { get; init; }
}

public sealed record ProxyImagePartObservation
{
    public required int Ordinal { get; init; }

    public required ProxyImageDisposition Disposition { get; init; }

    public required ProxyImageAvailability Availability { get; init; }

    public required ProxyImageReason AvailabilityReason { get; init; }

    public required string Format { get; init; }

    public string? DeclaredFormat { get; init; }

    public long? Width { get; init; }

    public long? Height { get; init; }

    public long? PixelCount { get; init; }

    public long? ImageEndOffset { get; init; }

    public long? TrailingByteCount { get; init; }

    public long? TrailingBytesObserved { get; init; }

    public bool? TrailingScanComplete { get; init; }

    public bool? TransparencyDeclared { get; init; }

    public required IReadOnlyList<ProxyImageFinding> Findings { get; init; }

    public required string SourceVersion { get; init; }
}

/// <summary>
/// One content observation. Identity is account-scoped and content-keyed; the association token is
/// only a parent link, never a unique key.
/// </summary>
public sealed record ProxyImageObservation
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required string MessageToken { get; init; }

    public required string ThreadToken { get; init; }

    public required MessageTrafficKind TrafficKind { get; init; }

    /// <summary>Keyed, length-prefixed identity of this content observation.</summary>
    public required string ObservationKey { get; init; }

    /// <summary>Keyed content fingerprint; never a plain content hash.</summary>
    public required string ContentFingerprint { get; init; }

    /// <summary>Persisted monotonic epoch assigned at startup.</summary>
    public required long ObservationEpoch { get; init; }

    /// <summary>Per-process monotonic sequence assigned by the owning observation context.</summary>
    public required long ObservationSequence { get; init; }

    /// <summary>Keyed digest of the canonical result; equal means an exact-repeat no-op.</summary>
    public required string ResultDigest { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required ProxyImageAvailability Availability { get; init; }

    public required ProxyImageReason AvailabilityReason { get; init; }

    public required int ImageParts { get; init; }

    public required int ImagesSkippedAsChrome { get; init; }

    public required int ImagesAdmitted { get; init; }

    public required int ImagesProduced { get; init; }

    public required int ImagesNotProduced { get; init; }

    public required int ImagesOverBudget { get; init; }

    public int? FirstOverBudgetOrdinal { get; init; }

    public int? LastOverBudgetOrdinal { get; init; }

    public required int EvidenceRowCount { get; init; }

    public required IReadOnlyList<ProxyImagePartObservation> Parts { get; init; }

    public required string SourceVersion { get; init; }

    /// <summary>Retention window recorded with the row; zero on a producer-built observation.</summary>
    public int RetentionDays { get; init; }
}

/// <summary>One parent association and its bounded content observations.</summary>
public sealed record ProxyImageMessage
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required string MessageToken { get; init; }

    public required string ThreadToken { get; init; }

    public required MessageTrafficKind TrafficKind { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required IReadOnlyList<ProxyImageObservation> Observations { get; init; }

    /// <summary>Observations beyond the first eight, if any. Never silently dropped.</summary>
    public int ChildrenOverflowCount { get; init; }

    /// <summary>Separate cursor for the parent's remaining observations, or null.</summary>
    public string? ChildrenNext { get; init; }
}

public sealed record ProxyImageMessagePage
{
    public required IReadOnlyList<ProxyImageMessage> Messages { get; init; }

    public required bool HasMore { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record ProxyImageChildPage
{
    public required IReadOnlyList<ProxyImageObservation> Observations { get; init; }

    public required bool HasMore { get; init; }

    public string? NextCursor { get; init; }
}

public interface IProxyImageObservationSink
{
    ValueTask RecordAsync(ProxyImageObservation observation, CancellationToken cancellationToken);
}

public interface IProxyImageObservationReader
{
    ValueTask<ProxyImageMessagePage> ListAsync(
        string tenantId,
        string threadToken,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        string? after,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<ProxyImageChildPage> ListChildrenAsync(
        string tenantId,
        string threadToken,
        string accountId,
        string messageToken,
        MessageTrafficKind trafficKind,
        int limit,
        string? after,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int maximumMessages,
        CancellationToken cancellationToken);
}
