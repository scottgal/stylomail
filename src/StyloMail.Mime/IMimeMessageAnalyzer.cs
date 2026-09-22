namespace StyloMail.Mime;

/// <summary>
/// Turns raw MIME bytes into a bounded analysis view plus deterministic evidence.
/// </summary>
/// <remarks>
/// Implementations <b>must not</b> perform network I/O of any kind: no fetching links, no loading
/// remote images, no executing attachments and no resolving redirects. That is a safety property
/// of the MVP, not a performance choice — resolving a link means touching an attacker-chosen host
/// from inside the trust boundary.
///
/// <para>
/// Implementations must also be deterministic: same bytes plus same request, same evidence.
/// Nothing here reads the wall clock directly; timestamps come from the injected
/// <see cref="MimeAnalysisRequest.TimeProvider"/>.
/// </para>
/// </remarks>
public interface IMimeMessageAnalyzer
{
    /// <summary>
    /// Parses and analyses one message. Never throws for hostile or malformed input — a message
    /// that cannot be read comes back as a rejection with an explicit disposition.
    /// </summary>
    MimeAnalysisResult Analyze(MimeAnalysisRequest request);
}
