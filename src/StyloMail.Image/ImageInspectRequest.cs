namespace StyloMail.Image;

/// <summary>
/// One image candidate, as bytes. Nothing here is rendered, decoded or fetched.
/// </summary>
/// <remarks>
/// <see cref="Content"/> may be a bounded prefix of a larger part. When it is,
/// <see cref="TotalLength"/> must carry the real size, so a coverage record can distinguish "the
/// file ends here" from "inspection stopped at its budget" and so offset arithmetic stays absolute
/// to the full content.
/// </remarks>
public sealed record ImageInspectRequest
{
    /// <summary>The part's declared content type, exactly as received. May be null or generic.</summary>
    public string? DeclaredContentType { get; init; }

    /// <summary>The bytes to inspect. At most <see cref="ImageInspectionLimits.MaxInspectedBytes"/> are read.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>
    /// Full size of the part when <see cref="Content"/> is a prefix. Null means the content is
    /// complete. Must not be smaller than the content supplied.
    /// </summary>
    public long? TotalLength { get; init; }
}
