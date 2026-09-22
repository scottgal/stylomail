namespace StyloMail.Mime;

/// <summary>
/// What the parser was able to make of the input.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="StyloMail.Core.AnalysisCoverage"/>. Coverage
/// describes how much of an analysable message was read; the disposition says whether there is an
/// analysable message at all. Anything other than <see cref="Parsed"/> means no
/// <see cref="StyloMail.Core.MailAnalysisInput"/> was produced, and the caller must apply an
/// explicit unsupported or oversize disposition rather than assess a fragment.
/// </remarks>
public enum MimeParseDisposition
{
    /// <summary>A complete message was parsed within limits and an analysis view was produced.</summary>
    Parsed = 0,

    /// <summary>The input was empty or contained no header/body separator, so it is not a message.</summary>
    Malformed = 1,

    /// <summary>The message exceeded the configured size ceiling.</summary>
    Oversize = 2,

    /// <summary>
    /// The message exceeded a structural limit (part count, nesting depth, header count or header
    /// size). No analysis view is produced, see the type remarks.
    /// </summary>
    LimitExceeded = 3,
}

/// <summary>Why a message was not parsed, and which limit was hit. Never carries message content.</summary>
public sealed record MimeParseRejection
{
    public required MimeParseDisposition Disposition { get; init; }

    /// <summary>
    /// Machine-readable reason, e.g. <c>part-count</c>, <c>mime-depth</c>,
    /// <c>no-header-body-separator</c>.
    /// </summary>
    /// <remarks>
    /// A limit reason suffixed <c>-after-parse</c> means the bounded pre-scan did not catch it and
    /// the breach was found by walking the parsed tree instead, the refusal still happened, but
    /// only after the parser had been handed the message. Callers watching for structural attacks
    /// should treat that as the more interesting of the two.
    /// </remarks>
    public required string Reason { get; init; }

    /// <summary>The configured limit that applied, when one did.</summary>
    public string? LimitName { get; init; }

    /// <summary>The observed value that breached it, when measurable.</summary>
    public long? Observed { get; init; }

    public long? Limit { get; init; }
}
