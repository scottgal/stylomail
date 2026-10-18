using StyloMail.Core;

namespace StyloMail.Image;

/// <summary>What the inspector was able to establish about one image candidate.</summary>
public enum ImageDisposition
{
    /// <summary>Structural facts were produced and every safety bound held.</summary>
    Inspected = 0,

    /// <summary>
    /// Structural facts were produced, but a hard bound (dimensions, pixels) was crossed. The
    /// image must not be decoded or rendered; the refusal itself is the evidence.
    /// </summary>
    Refused = 1,

    /// <summary>No structural facts could be produced. <see cref="ImageInspectionResult.NotProducedReason"/> names why.</summary>
    NotProduced = 2,
}

/// <summary>
/// Why an inspection produced nothing. A named reason, never a zero score and never an exception.
/// </summary>
public enum ImageNotProducedReason
{
    /// <summary>The part carried no bytes at all.</summary>
    EmptyContent = 0,

    /// <summary>Neither a declaration nor a signature says this is an image.</summary>
    NotAnImage = 1,

    /// <summary>The format is recognised but this build does not interpret it.</summary>
    UnsupportedFormat = 2,

    /// <summary>The declared type and the magic bytes disagree, so the declaration cannot be trusted.</summary>
    FormatMismatch = 3,

    /// <summary>The available bytes did not contain a complete header.</summary>
    TruncatedHeader = 4,

    /// <summary>The bounded chunk/segment walk was exhausted before the structure could be read.</summary>
    HeaderBudgetExceeded = 5,

    /// <summary>The header declared zero or otherwise invalid dimensions.</summary>
    InvalidDimensions = 6,

    /// <summary>The bytes are not consistent with the declared container's structure.</summary>
    InvalidStructure = 7,

    /// <summary>
    /// A prefix was supplied without enough decoded bytes to make even a structural claim. Distinct from
    /// <see cref="EmptyContent"/>: the part may be non-empty, but the decoded prefix is not usable.
    /// </summary>
    InsufficientDecodedLength = 8,

    /// <summary>
    /// The image was not inspected because the per-message admission budget was exhausted. Used only on
    /// the aggregate coverage row, never as a per-image result.
    /// </summary>
    InspectionBudgetExhausted = 9,
}

/// <summary>A closed vocabulary of structural findings. Findings are facts, not verdicts.</summary>
public enum ImageFindingCode
{
    /// <summary>Declared content type and magic bytes name different formats.</summary>
    TypeMagicMismatch = 0,

    /// <summary>Bytes follow the container's terminator.</summary>
    TrailingData = 1,

    /// <summary>An archive signature was found in the trailing region.</summary>
    EmbeddedArchive = 2,

    /// <summary>An executable signature was found in the trailing region.</summary>
    EmbeddedExecutable = 3,

    /// <summary>The format itself can carry script and external references (SVG).</summary>
    ScriptableFormat = 4,

    /// <summary>The header declared a zero dimension.</summary>
    ZeroDimensions = 5,

    /// <summary>A dimension exceeds <see cref="ImageInspectionLimits.MaxDimension"/>.</summary>
    DimensionLimitExceeded = 6,

    /// <summary>The pixel count exceeds <see cref="ImageInspectionLimits.MaxPixels"/>.</summary>
    PixelLimitExceeded = 7,
}

/// <summary>
/// One named fact about the bytes. <see cref="Offset"/> is absolute to the full content, never to
/// the inspected prefix.
/// </summary>
public sealed record ImageFinding
{
    public required ImageFindingCode Code { get; init; }

    public long? Offset { get; init; }

    /// <summary>Short, fixed detail such as a signature name. Never image content.</summary>
    public string? Detail { get; init; }
}

/// <summary>How much of the part was actually inspected.</summary>
public sealed record ImageInspectionCoverage
{
    /// <summary>Bytes examined, bounded by <see cref="ImageInspectionLimits.MaxInspectedBytes"/>.</summary>
    public required long InspectedBytes { get; init; }

    /// <summary>Full part size, or null when <see cref="ImageContentExtent.PrefixTotalUnknown"/> was supplied.</summary>
    public required long? TotalBytes { get; init; }

    /// <summary>True when <see cref="TotalBytes"/> is a real total rather than an unknown.</summary>
    public required bool TotalBytesKnown { get; init; }

    /// <summary>True when the part is larger than the inspected prefix, or its total is unknown.</summary>
    public required bool Truncated { get; init; }

    /// <summary>True when a dimension header was read.</summary>
    public required bool HeaderParsed { get; init; }

    /// <summary>True when the container's terminator (or its declared total size) was located.</summary>
    public required bool TerminatorFound { get; init; }
}

/// <summary>
/// The outcome of a bounded structural inspection. Carries no image bytes, only identities,
/// dimensions, offsets, findings and availability.
/// </summary>
public sealed record ImageInspectionResult
{
    public required string? DeclaredContentType { get; init; }

    public required ImageFormat DeclaredFormat { get; init; }

    public required ImageFormat DetectedFormat { get; init; }

    /// <summary>True when a specific declaration and the magic bytes name different formats.</summary>
    public required bool FormatMismatch { get; init; }

    public required ImageDisposition Disposition { get; init; }

    /// <summary>Set exactly when <see cref="Disposition"/> is <see cref="ImageDisposition.NotProduced"/>.</summary>
    public ImageNotProducedReason? NotProducedReason { get; init; }

    /// <summary>
    /// Whether a measurement exists. <see cref="EvidenceAvailability.ReducedCoverage"/> means the
    /// facts are real but were measured over a prefix, never that they are a full-file result.
    /// </summary>
    public required EvidenceAvailability Availability { get; init; }

    public long? Width { get; init; }

    public long? Height { get; init; }

    public long? PixelCount { get; init; }

    /// <summary>Absolute offset just past the container terminator, when one was located.</summary>
    public long? ImageEndOffset { get; init; }

    /// <summary>
    /// Exact bytes after <see cref="ImageEndOffset"/>, when it is known. Null means no claim:
    /// either no terminator was found, the terminator fell outside the inspected prefix, or the
    /// part's total length is unknown.
    /// </summary>
    public long? TrailingByteCount { get; init; }

    /// <summary>
    /// Lower bound on trailing bytes observed inside the inspected prefix when the total length is
    /// unknown. Null when no terminator was located or when <see cref="TrailingByteCount"/> is exact.
    /// </summary>
    public long? TrailingBytesObserved { get; init; }

    /// <summary>True only when the whole trailing region was inside the inspected prefix and the total is known.</summary>
    public bool TrailingScanComplete { get; init; }

    /// <summary>
    /// Header-level transparency declaration: true for PNG alpha/tRNS, GIF transparency, WebP VP8X alpha.
    /// False when the format says there is none. Null when the header did not establish it.
    /// </summary>
    public bool? TransparencyDeclared { get; init; }

    public required IReadOnlyList<ImageFinding> Findings { get; init; }

    public required ImageInspectionCoverage Coverage { get; init; }

    /// <summary>
    /// True when the image passed the structural bounds. Consumers must still check
    /// <see cref="ImageInspectionCoverage.Truncated"/> before treating a decode as informed.
    /// </summary>
    public bool Decodable => Disposition == ImageDisposition.Inspected;
}
