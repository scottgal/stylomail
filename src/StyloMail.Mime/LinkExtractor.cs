using StyloMail.Core;

namespace StyloMail.Mime;

/// <summary>
/// Finds the link candidates in a parsed message and hands them to the shared link analysis.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is MIME-specific here is only where the candidates come from.</b> An HTML part yields
/// anchors whose label is written separately from their target, and the plain body yields bare URLs.
/// Everything done with them afterwards, the label-versus-destination comparison and the
/// internationalised-domain inspection beneath it, is true of a link on any channel, so it lives in
/// <see cref="LinkAnalysis"/> rather than here.
/// </para>
/// <para>
/// Keeping this adapter rather than calling <see cref="LinkAnalysis"/> from the analyzer directly
/// keeps the knowledge of "an HTML part carries anchors, a body carries bare URLs" in the project
/// that owns HTML parsing.
/// </para>
/// </remarks>
internal static class LinkExtractor
{
    public static IReadOnlyList<LinkFinding> Extract(
        HtmlAnalysis html,
        string plainBody,
        MimeParseLimits limits) =>
        LinkAnalysis.Extract(
            html.Links.Select(link => (link.Href, link.Label)),
            plainBody,
            limits.MaxLinks);
}
