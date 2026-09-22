namespace StyloMail.Core;

/// <summary>A link as it appears to the reader, versus where it actually points.</summary>
/// <remarks>
/// Displayed-versus-actual mismatch is one of the highest-value deterministic signals, so the
/// two are recorded separately rather than collapsed. Redirects are <b>not</b> followed in the
/// MVP, resolving them requires a separately sandboxed, SSRF-resistant fetcher.
/// </remarks>
public sealed record LinkObservation
{
    public required string DisplayedText { get; init; }

    public required string ActualTarget { get; init; }

    /// <summary>Host in its Unicode form, when it differs from the ASCII/punycode form.</summary>
    public string? UnicodeHost { get; init; }

    /// <summary>Host as transmitted (possibly punycode), for IDN homograph comparison.</summary>
    public string? AsciiHost { get; init; }
}

/// <summary>Metadata about an attachment. Contents are not opened or executed.</summary>
public sealed record AttachmentMetadata
{
    public required string FileName { get; init; }

    /// <summary>Declared content type.</summary>
    public required string DeclaredContentType { get; init; }

    /// <summary>Type inferred from the file name extension.</summary>
    public string? ExtensionImpliedContentType { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>
    /// False when <see cref="SizeBytes"/> is a partial count, typically bytes hashed before the
    /// hashing budget was exhausted. Defaults true, so a complete measurement needs no ceremony and
    /// a partial one is explicit rather than silently indistinguishable.
    /// </summary>
    public bool SizeBytesIsComplete { get; init; } = true;

    /// <summary>Digest of the attachment bytes, for campaign grouping by attachment hash.</summary>
    public string? ContentHash { get; init; }

    /// <summary>True when the attachment could not be parsed, encrypted, password-protected or malformed.</summary>
    public required bool ContentUnavailable { get; init; }
}

/// <summary>
/// How much of the message was actually analysable. Recorded so that reduced coverage is
/// visible in the decision rather than silently producing a confident-looking result.
/// </summary>
public sealed record AnalysisCoverage
{
    public required bool BodyParsed { get; init; }

    public required bool HtmlPresent { get; init; }

    public required bool HasAttachments { get; init; }

    /// <summary>True when text and HTML representations disagreed materially.</summary>
    public required bool HtmlTextDisagreement { get; init; }

    /// <summary>True when the message could not be fully parsed within configured limits.</summary>
    public required bool ParserLimitExceeded { get; init; }

    /// <summary>
    /// True when the limit exceeded was a size limit, as distinct from a structural one (part
    /// count, nesting depth, header count). An oversize message is a different operational problem
    /// from a hostile or malformed structure, and collapsing both into
    /// <see cref="ParserLimitExceeded"/> loses that distinction in the ledger.
    /// </summary>
    public bool OversizeRejected { get; init; }

    /// <summary>True when encrypted or password-protected content reduced the analysable portion.</summary>
    public required bool ContentEncrypted { get; init; }

    public required bool Truncated { get; init; }

    /// <summary>True when no bounded conversation context was available.</summary>
    public required bool ConversationContextMissing { get; init; }
}

/// <summary>
/// The normalised analysis view of a message.
/// </summary>
/// <remarks>
/// This is derived from a copy. The original MIME bytes are retained separately for transport
/// and signature integrity and are never replaced by this view, rewriting signed content
/// invalidates DKIM, and a proxy that quietly rewrites mail is a proxy that breaks the
/// guarantees it was deployed to uphold.
///
/// <para>
/// Everything here is <b>untrusted data</b>, including any imperative text in the body.
/// Content is supplied to the classifier as data, and instructions embedded in a message
/// carry no authority over the assessment.
/// </para>
/// </remarks>
public sealed record MailAnalysisInput
{
    public required MailEnvelope Envelope { get; init; }

    public required AuthenticationContext Authentication { get; init; }

    /// <summary>
    /// The channel this message arrived on, and what it tells us about reach.
    /// </summary>
    /// <remarks>
    /// Required, so the adapter that produced this input names the channel rather than leaving a
    /// reader to infer it from the shape of the message.
    /// </remarks>
    public required ChannelContext Channel { get; init; }

    public string? Subject { get; init; }

    /// <summary>New body text, with quoted history removed where it could be identified.</summary>
    public required string BodyText { get; init; }

    /// <summary>Quoted or forwarded material, labelled separately so the classifier can weigh it differently.</summary>
    public string? QuotedText { get; init; }

    public required IReadOnlyList<LinkObservation> Links { get; init; }

    public required IReadOnlyList<AttachmentMetadata> Attachments { get; init; }

    /// <summary>Bounded prior conversation, when available. Absence makes conversational continuity NotApplicable.</summary>
    public IReadOnlyList<string>? ConversationContext { get; init; }

    public required AnalysisCoverage Coverage { get; init; }
}
