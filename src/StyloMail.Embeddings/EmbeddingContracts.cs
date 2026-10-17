namespace StyloMail.Embeddings;

/// <summary>
/// Whether a segment's vector was actually produced.
/// </summary>
/// <remarks>
/// <b>There is no third state and no zero vector.</b> A zero vector is a real point in the space and
/// would pull a centroid toward it, so an outage that reported zeroes would poison exactly the
/// comparison the vectors exist for. Absence is carried as absence, with the reason beside it.
/// </remarks>
public enum SegmentEmbeddingAvailability
{
    /// <summary>A unit-length vector at the configured dimension is present.</summary>
    Available = 0,

    /// <summary>No vector was produced. <see cref="SegmentEmbedding.Reason"/> names why.</summary>
    NotProduced = 1,
}

/// <summary>
/// Why a vector was not produced, in a closed vocabulary the management surface can render.
/// </summary>
/// <remarks>
/// These names travel on the wire in derived signal rows, so they are stable identifiers rather
/// than prose. <see cref="EmbeddingReasons.ToWireName"/> is the single place that spells them.
/// </remarks>
public enum EmbeddingNotProducedReason
{
    /// <summary>No reason. Present only on an available row.</summary>
    None = 0,

    /// <summary>The service refused the connection, or DNS or routing failed.</summary>
    ServiceUnreachable,

    /// <summary>The call exceeded <see cref="EmbeddingOptions.Timeout"/>.</summary>
    TimedOut,

    /// <summary>The service rejected the bearer token.</summary>
    Unauthorized,

    /// <summary>The token file is missing, empty or unreadable.</summary>
    TokenUnavailable,

    /// <summary>The service answered 5xx after the retry, or a status with no better meaning.</summary>
    ServiceUnavailable,

    /// <summary>The service refused the batch. A client and service bound disagree.</summary>
    RequestRejected,

    /// <summary>Metadata could not be read, so nothing could be verified before embedding.</summary>
    MetadataUnavailable,

    /// <summary>The service's model tag differs from the configured one.</summary>
    ModelTagMismatch,

    /// <summary>The service's dimension differs from the configured one.</summary>
    DimensionMismatch,

    /// <summary>The service's prefix version differs from the configured one.</summary>
    PrefixVersionMismatch,

    /// <summary>The service reports a dtype this provider will not accept, notably float16.</summary>
    UnsupportedDtype,

    /// <summary>The response did not match the documented shape.</summary>
    ContractViolation,

    /// <summary>A vector contained a value that is not finite.</summary>
    NonFiniteVector,

    /// <summary>A vector was not unit length, so it cannot be compared as a cosine direction.</summary>
    VectorNotNormalised,

    /// <summary>The batch exceeded <see cref="EmbeddingOptions.MaxSegments"/>.</summary>
    TooManySegments,

    /// <summary>A segment was empty or whitespace.</summary>
    EmptySegment,

    /// <summary>A segment exceeded <see cref="EmbeddingOptions.MaxSegmentCharacters"/>.</summary>
    SegmentTooLong,

    /// <summary>The circuit is open after repeated failures.</summary>
    CircuitOpen,
}

/// <summary>Wire spellings for <see cref="EmbeddingNotProducedReason"/>.</summary>
public static class EmbeddingReasons
{
    /// <summary>
    /// The stable snake case name of a reason, suitable for a stored signal row or an API field.
    /// </summary>
    public static string ToWireName(this EmbeddingNotProducedReason reason) => reason switch
    {
        EmbeddingNotProducedReason.None => "none",
        EmbeddingNotProducedReason.ServiceUnreachable => "service_unreachable",
        EmbeddingNotProducedReason.TimedOut => "timed_out",
        EmbeddingNotProducedReason.Unauthorized => "unauthorized",
        EmbeddingNotProducedReason.TokenUnavailable => "token_unavailable",
        EmbeddingNotProducedReason.ServiceUnavailable => "service_unavailable",
        EmbeddingNotProducedReason.RequestRejected => "request_rejected",
        EmbeddingNotProducedReason.MetadataUnavailable => "metadata_unavailable",
        EmbeddingNotProducedReason.ModelTagMismatch => "model_tag_mismatch",
        EmbeddingNotProducedReason.DimensionMismatch => "dimension_mismatch",
        EmbeddingNotProducedReason.PrefixVersionMismatch => "prefix_version_mismatch",
        EmbeddingNotProducedReason.UnsupportedDtype => "unsupported_dtype",
        EmbeddingNotProducedReason.ContractViolation => "contract_violation",
        EmbeddingNotProducedReason.NonFiniteVector => "non_finite_vector",
        EmbeddingNotProducedReason.VectorNotNormalised => "vector_not_normalised",
        EmbeddingNotProducedReason.TooManySegments => "too_many_segments",
        EmbeddingNotProducedReason.EmptySegment => "empty_segment",
        EmbeddingNotProducedReason.SegmentTooLong => "segment_too_long",
        EmbeddingNotProducedReason.CircuitOpen => "circuit_open",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no wire name for this reason"),
    };
}

/// <summary>
/// What produced a vector set, carried beside the vectors rather than inferred from configuration.
/// </summary>
/// <remarks>
/// A stored vector is only comparable with another at the same model tag, dimension and prefix
/// version, so those three travel with the data. <see cref="ModelRevision"/> may be null: a local
/// checkpoint does not always carry the commit it was exported from, and a provider that invented
/// one would make an unversioned vector look versioned.
/// </remarks>
public sealed record EmbeddingMetadata(
    string ModelTag,
    string? ModelRevision,
    int Dimension,
    int NativeDimension,
    string PrefixVersion,
    string Dtype,
    string Contract);

/// <summary>One segment's outcome.</summary>
/// <param name="Ordinal">Zero based position in the request batch, so a row can be matched back.</param>
public sealed record SegmentEmbedding(
    int Ordinal,
    SegmentEmbeddingAvailability Availability,
    float[]? Vector,
    EmbeddingNotProducedReason Reason)
{
    public bool IsAvailable =>
        Availability == SegmentEmbeddingAvailability.Available && Vector is not null;

    /// <summary>An available row, at the configured dimension.</summary>
    public static SegmentEmbedding Produced(int ordinal, float[] vector) =>
        new(ordinal, SegmentEmbeddingAvailability.Available, vector, EmbeddingNotProducedReason.None);

    /// <summary>An absent row, with its reason. Never a zero vector.</summary>
    public static SegmentEmbedding Absent(int ordinal, EmbeddingNotProducedReason reason) =>
        new(ordinal, SegmentEmbeddingAvailability.NotProduced, null, reason);
}

/// <summary>The outcome of one batch, one row per input segment in input order.</summary>
public sealed record SegmentEmbeddingBatch(
    IReadOnlyList<SegmentEmbedding> Segments,
    EmbeddingMetadata? Metadata)
{
    /// <summary>True when every requested row was produced.</summary>
    public bool AllProduced => Segments.All(segment => segment.IsAvailable);

    /// <summary>True when at least one row was produced.</summary>
    public bool AnyProduced => Segments.Any(segment => segment.IsAvailable);

    /// <summary>
    /// The distinct reasons any row was absent, in wire spelling, for a signal row or a log field.
    /// </summary>
    public IReadOnlyList<string> AbsenceReasons =>
        Segments
            .Where(segment => !segment.IsAvailable)
            .Select(segment => segment.Reason.ToWireName())
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// The embedding port. One implementation today, a local HTTP service, and the interface exists so a
/// caller depends on the contract rather than on that transport.
/// </summary>
public interface ISegmentEmbeddingProvider
{
    /// <summary>
    /// The service's metadata, when it was last read and validated. Null until a call has succeeded.
    /// </summary>
    EmbeddingMetadata? Metadata { get; }

    /// <summary>
    /// Read and validate metadata without embedding anything. Returns null when the service does not
    /// match the configuration, which is also what a subsequent <see cref="EmbedAsync"/> reports.
    /// </summary>
    Task<EmbeddingMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed a bounded batch of segments. One row is returned per input, in input order. A failure
    /// produces absent rows with a reason; it does not throw for an operational fault and it never
    /// substitutes a zero vector.
    /// </summary>
    Task<SegmentEmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default);
}
