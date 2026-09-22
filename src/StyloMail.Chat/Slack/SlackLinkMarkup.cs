using System.Text.RegularExpressions;
using StyloMail.Core;

namespace StyloMail.Chat.Slack;

/// <summary>
/// A link as the message presented it: what a reader saw, and where it actually goes.
/// </summary>
public sealed record PresentedLink
{
    public required string DisplayedText { get; init; }

    public required string ActualTarget { get; init; }
}

/// <summary>
/// Reads the links out of a Slack message body, markup and plain text alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the chat analogue of reading anchors out of HTML, and it is the reason the shared link
/// analysis could not be reused unchanged.</b> Slack writes a link as
/// <c>&lt;https://example.com|example&gt;</c>, so the display text and the destination arrive in one
/// string joined by a separator, and a reader that treated that as one URL would destroy the
/// comparison before anything had a chance to make it.
/// </para>
/// <para>
/// <b>What this produces is an observation and not a judgement.</b> It reports what the message
/// contained. Whether a label disagrees with its destination is decided later, by
/// <see cref="ChatEvidenceProducer"/> over the shared analysis in <see cref="LinkAnalysis"/>.
/// </para>
/// </remarks>
public static partial class SlackLinkMarkup
{
    /// <summary>
    /// The platform's link syntax. The target stops at the first separator, so everything after it
    /// is the label, which is what the platform renders and may itself contain a separator.
    /// </summary>
    [GeneratedRegex(
        @"<((?:https?://|mailto:)[^>|]*)(?:\|([^>]*))?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Markup();

    /// <summary>Every link in the message, in the order it appears.</summary>
    public static IReadOnlyList<PresentedLink> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var links = new List<PresentedLink>();

        foreach (Match match in Markup().Matches(text))
        {
            var target = match.Groups[1].Value;
            var label = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;

            links.Add(new PresentedLink
            {
                ActualTarget = target,

                // No label means the platform renders the destination itself, so that is the display
                // text and it cannot disagree with itself.
                DisplayedText = label.Length > 0 ? label : target,
            });
        }

        // Bare URLs are read from a copy with the markup blanked out rather than by matching URLs
        // again here. The scan is LinkAnalysis's, so writing a second one would be a second pattern
        // to keep in step with the first, and blanking is what stops a marked-up destination being
        // counted twice.
        foreach (var bare in LinkAnalysis.BareUrlsIn(Blank(text)))
        {
            links.Add(new PresentedLink { ActualTarget = bare, DisplayedText = bare });
        }

        return links;
    }

    /// <summary>Replaces each marked-up link with spaces, preserving every other character's position.</summary>
    private static string Blank(string text)
    {
        var characters = text.ToCharArray();

        foreach (Match match in Markup().Matches(text))
        {
            for (var i = match.Index; i < match.Index + match.Length; i++)
            {
                characters[i] = ' ';
            }
        }

        return new string(characters);
    }
}
