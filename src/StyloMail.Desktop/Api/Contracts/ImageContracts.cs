namespace StyloMail.Desktop.Api.Contracts;

/// <summary>One thread's image observations in the fixed window. No image bytes are served.</summary>
public sealed record ConversationImagesResponse
{
    public required string TenantId { get; init; }
    public required string ThreadToken { get; init; }
    public required string Window { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required string Provenance { get; init; }
    public required bool ImagesEnabled { get; init; }
    public required int RetentionDays { get; init; }
    public required IReadOnlyList<ImageMessageRow> Messages { get; init; }
    public string? Next { get; init; }
}

public sealed record ImageMessageRow
{
    public required string MessageToken { get; init; }
    public required string AccountId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string TrafficKind { get; init; }
    public required string State { get; init; }
    public required IReadOnlyList<ImageObservationRow> Observations { get; init; }
    public required int ChildrenOverflowCount { get; init; }
    public string? ChildrenNext { get; init; }
}

public sealed record ImageObservationRow
{
    public required string ObservationKey { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string State { get; init; }
    public string? Availability { get; init; }
    public string? AvailabilityReason { get; init; }
    public ImageCountsRow? Counts { get; init; }
    public int? FirstOverBudgetOrdinal { get; init; }
    public int? LastOverBudgetOrdinal { get; init; }
    public string? SourceVersion { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required IReadOnlyList<ImagePartRow> Parts { get; init; }
}

public sealed record ImageCountsRow
{
    public int ImageParts { get; init; }
    public int ImagesSkippedAsChrome { get; init; }
    public int ImagesAdmitted { get; init; }
    public int ImagesProduced { get; init; }
    public int ImagesNotProduced { get; init; }
    public int ImagesOverBudget { get; init; }
}

public sealed record ImagePartRow
{
    public int Ordinal { get; init; }
    public string? Disposition { get; init; }
    public string? Availability { get; init; }
    public string? AvailabilityReason { get; init; }
    public string? Format { get; init; }
    public string? DeclaredFormat { get; init; }
    public long? Width { get; init; }
    public long? Height { get; init; }
    public long? PixelCount { get; init; }
    public long? ImageEndOffset { get; init; }
    public long? TrailingByteCount { get; init; }
    public long? TrailingBytesObserved { get; init; }
    public bool? TrailingScanComplete { get; init; }
    public bool? TransparencyDeclared { get; init; }
    public string? SourceVersion { get; init; }
    public IReadOnlyList<ImageFindingRow> Findings { get; init; } = [];
}

public sealed record ImageFindingRow
{
    public string? Code { get; init; }
    public long? Offset { get; init; }
    public string? Detail { get; init; }
}
