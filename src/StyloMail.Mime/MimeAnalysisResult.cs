using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>
/// The outcome of analysing one message: an analysis view plus the deterministic evidence
/// extracted from it.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is <see langword="null"/> whenever <see cref="Disposition"/> is anything
/// other than <see cref="MimeParseDisposition.Parsed"/>. That is the point: a message that blew a
/// limit produces a rejection and a coverage record, never a half-read fragment dressed up as a
/// complete message.
/// </remarks>
public sealed record MimeAnalysisResult
{
    public required MimeParseDisposition Disposition { get; init; }

    /// <summary>The analysis view, or <see langword="null"/> when the message was not analysable.</summary>
    public MailAnalysisInput? Message { get; init; }

    /// <summary>
    /// Deterministic evidence. Produced even when the message was rejected, so the ledger records
    /// what the parser saw (size, structure, coverage) rather than only that it declined.
    /// </summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>Coverage achieved. For a rejected message this records the limit that was hit.</summary>
    public required AnalysisCoverage Coverage { get; init; }

    public MimeParseRejection? Rejection { get; init; }

    /// <summary>True when an analysis view was produced.</summary>
    public bool IsAnalysable => Disposition == MimeParseDisposition.Parsed && Message is not null;
}
