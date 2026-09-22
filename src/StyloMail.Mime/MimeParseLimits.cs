using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>
/// Hard bounds applied to parsing. These are safety limits, not tuning knobs: they exist so that
/// a hostile or broken message cannot turn parsing into unbounded work, and so that when a message
/// does not fit we can say so instead of analysing a fragment.
/// </summary>
/// <remarks>
/// Every limit has a matching way of being reported. Exceeding a structural limit yields a
/// <see cref="MimeParseDisposition.LimitExceeded"/> disposition rather than a partial parse,
/// because a partially parsed message analysed as though it were whole is exactly the failure
/// mode the source specification calls out.
/// </remarks>
public sealed record MimeParseLimits
{
    /// <summary>Default limits, sized for ordinary business mail with generous headroom.</summary>
    public static readonly MimeParseLimits Default = new();

    /// <summary>Largest message accepted for parsing at all.</summary>
    public long MaxMessageBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Largest number of MIME entities (parts) in the tree.</summary>
    public int MaxParts { get; init; } = 512;

    /// <summary>Largest nesting depth of multipart containers.</summary>
    public int MaxMimeDepth { get; init; } = 16;

    /// <summary>Largest number of header fields on the outer message.</summary>
    public int MaxHeaderCount { get; init; } = 512;

    /// <summary>Largest total size of the outer header block.</summary>
    public long MaxHeaderBytes { get; init; } = 256 * 1024;

    /// <summary>Largest single header line, measured before unfolding.</summary>
    public int MaxHeaderLineLength { get; init; } = 16 * 1024;

    /// <summary>Largest number of links recorded from a message.</summary>
    public int MaxLinks { get; init; } = 512;

    /// <summary>Largest number of attachments recorded from a message.</summary>
    public int MaxAttachments { get; init; } = 128;

    /// <summary>Largest decoded size of any single text body, in characters.</summary>
    public int MaxBodyChars { get; init; } = 2 * 1024 * 1024;

    /// <summary>Largest total decoded bytes hashed across all attachments.</summary>
    public long MaxTotalAttachmentHashBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Largest decoded size hashed for a single attachment; beyond this the hash is marked partial.</summary>
    public long MaxAttachmentHashBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Largest number of attribute entries carried on any one piece of evidence.</summary>
    /// <summary>
    /// The attribute budget handed to the shared evidence builder.
    /// </summary>
    /// <remarks>
    /// Mapped rather than passed directly, because the builder is in Core and Core does not know what
    /// a MIME parser needs. Keeping the two numbers named separately here is what lets a
    /// parser-shaped limit change without moving the evidence contract with it.
    /// </remarks>
    public EvidenceAttributeLimits AttributeBudget =>
        new() { MaxEntries = MaxAttributeEntries, MaxValueLength = MaxAttributeValueLength };

    public int MaxAttributeEntries { get; init; } = 24;

    /// <summary>Largest length of an attribute value.</summary>
    public int MaxAttributeValueLength { get; init; } = 512;
}
