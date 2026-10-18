namespace StyloMail.Host.Contracts;

/// <summary>One thread's image observation parents within the fixed window.</summary>
public sealed record ProxyConversationImagesResponse
{
    public required string TenantId { get; init; }

    public required string ThreadToken { get; init; }

    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required string Provenance { get; init; }

    /// <summary>Whether new image observation is enabled. Missing rows are never rendered as clean.</summary>
    public required bool ImagesEnabled { get; init; }

    public required int RetentionDays { get; init; }

    public required IReadOnlyList<ProxyImageMessageResponse> Messages { get; init; }

    /// <summary>Parent page cursor only; children use their own childrenNext.</summary>
    public string? Next { get; init; }
}

/// <summary>One parent association, with up to eight content observations.</summary>
public sealed record ProxyImageMessageResponse
{
    public required string MessageToken { get; init; }

    public required string AccountId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public required string TrafficKind { get; init; }

    /// <summary>Aggregate over observations; disabled/no_observation when there is no child.</summary>
    public required string State { get; init; }

    public required IReadOnlyList<ProxyImageObservationResponse> Observations { get; init; }

    /// <summary>Observations beyond the first eight, if any. Never silently dropped.</summary>
    public required int ChildrenOverflowCount { get; init; }

    /// <summary>Separate child cursor for the overflow, or null.</summary>
    public string? ChildrenNext { get; init; }
}

/// <summary>One content observation, distinct from its parent association token.</summary>
public sealed record ProxyImageObservationResponse
{
    public required string ObservationKey { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Exhaustive state for this observation.</summary>
    public required string State { get; init; }

    public string? Availability { get; init; }

    public string? AvailabilityReason { get; init; }

    public ProxyImageCountsResponse? Counts { get; init; }

    public int? FirstOverBudgetOrdinal { get; init; }

    public int? LastOverBudgetOrdinal { get; init; }

    public string? SourceVersion { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required IReadOnlyList<ProxyImagePartResponse> Parts { get; init; }
}

public sealed record ProxyImageCountsResponse
{
    public required int ImageParts { get; init; }

    public required int ImagesSkippedAsChrome { get; init; }

    public required int ImagesAdmitted { get; init; }

    public required int ImagesProduced { get; init; }

    public required int ImagesNotProduced { get; init; }

    public required int ImagesOverBudget { get; init; }
}

public sealed record ProxyImagePartResponse
{
    public required int Ordinal { get; init; }

    public required string Disposition { get; init; }

    public required string Availability { get; init; }

    public required string AvailabilityReason { get; init; }

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

    public required string SourceVersion { get; init; }

    public required IReadOnlyList<ProxyImageFindingResponse> Findings { get; init; }
}

public sealed record ProxyImageFindingResponse
{
    public required string Code { get; init; }

    public long? Offset { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Child continuation page for one parent association.</summary>
public sealed record ProxyImageChildrenResponse
{
    public required string TenantId { get; init; }

    public required string ThreadToken { get; init; }

    public required string AccountId { get; init; }

    public required string MessageToken { get; init; }

    public required string TrafficKind { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required IReadOnlyList<ProxyImageObservationResponse> Observations { get; init; }

    public string? Next { get; init; }
}
