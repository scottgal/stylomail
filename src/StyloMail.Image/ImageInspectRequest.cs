namespace StyloMail.Image;

/// <summary>
/// What the supplied bytes are, and whether their full length is known.
/// </summary>
/// <remarks>
/// <see cref="Complete"/> is the only state in which the inspector may treat the supplied content
/// as the whole part. A caller holding a decoded prefix must say so and must not encode an unknown
/// length as the prefix's own size.
/// </remarks>
public enum ImageContentExtent
{
    /// <summary>The supplied content is the whole part; <see cref="ImageInspectRequest.TotalLength"/> must be null.</summary>
    Complete = 0,

    /// <summary>The content is a prefix and <see cref="ImageInspectRequest.TotalLength"/> carries the real size.</summary>
    PrefixTotalKnown = 1,

    /// <summary>The content is a prefix and the real size is unknown; no total or trailing count may be claimed.</summary>
    PrefixTotalUnknown = 2,
}

/// <summary>
/// One image candidate, as bytes. Nothing here is rendered, decoded or fetched.
/// </summary>
/// <remarks>
/// <see cref="Content"/> may be a bounded prefix of a larger part. The extent says which it is:
/// complete, a prefix with a known total, or a prefix whose total is unknown. Contradictory
/// extent/length combinations are refused with <see cref="ArgumentException"/> before anything is
/// allocated, because silently treating a prefix as complete would fabricate a trailing result.
/// </remarks>
public sealed record ImageInspectRequest
{
    /// <summary>The part's declared content type, exactly as received. May be null or generic.</summary>
    public string? DeclaredContentType { get; init; }

    /// <summary>The bytes to inspect. At most <see cref="ImageInspectionLimits.MaxInspectedBytes"/> are read.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>Whether the content is complete, a prefix with a known total, or a prefix with an unknown total.</summary>
    public ImageContentExtent Extent { get; init; } = ImageContentExtent.Complete;

    /// <summary>
    /// Full size of the part when <see cref="Extent"/> is <see cref="ImageContentExtent.PrefixTotalKnown"/>.
    /// Must be null for <see cref="ImageContentExtent.Complete"/> and
    /// <see cref="ImageContentExtent.PrefixTotalUnknown"/>, and must not be smaller than the content supplied.
    /// </summary>
    public long? TotalLength { get; init; }
}
