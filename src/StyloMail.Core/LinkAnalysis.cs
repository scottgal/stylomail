using System.Text.RegularExpressions;

namespace StyloMail.Core;

/// <summary>One link, with the comparison between what it says and where it goes.</summary>
public sealed record LinkFinding
{
    public required string DisplayedText { get; init; }

    public required string ActualTarget { get; init; }

    public required UrlObservation? Target { get; init; }

    public required IdnObservation? Idn { get; init; }

    public required bool DisplayMismatch { get; init; }

    /// <summary>How the label disagreed, <c>label-host</c>, <c>label-email</c>, <c>label-scheme</c>.</summary>
    public string? MismatchKind { get; init; }

    /// <summary>
    /// True when the visible label itself names a destination, so it <em>can</em> disagree.
    /// "Click here" claims nothing; "paypal.com" claims a host. Only claiming labels belong in the
    /// denominator, or the ratio would be diluted by labels that were never in play.
    /// </summary>
    public required bool LabelMakesHostClaim { get; init; }
}

/// <summary>
/// Finds links and compares the label against the destination. No network access of any kind.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is textual. A label reading <c>paypal.com</c> over a target of
/// <c>paypal.com.evil.example</c> is a mismatch because the two hosts differ as written, and the
/// claim about intent stays with policy. What matters is that the label and the target are captured
/// separately and never collapsed: a great many detections are lost by storing only one of them.
/// </para>
/// <para>
/// <b>This lives in Core rather than in the MIME adapter, where it was written, and
/// <see cref="FromPlainText"/> is why.</b> A chat message has no HTML part and no envelope, so its
/// text is the only place a link can be found. The alternative to sharing this was a chat connector
/// that depended on a MIME parser to find URLs, or a second copy of the label-versus-host
/// comparison, and a heuristic with two copies has one copy nobody re-reads.
/// </para>
/// <para>
/// A channel that has a display text separate from the target, as HTML anchors do and as Slack's
/// own markup does, passes both through <see cref="Extract"/>; a channel whose text merely contains
/// URLs uses <see cref="FromPlainText"/>, where the URL is its own label and so cannot disagree
/// with itself.
/// </para>
/// </remarks>
public static partial class LinkAnalysis
{
    /// <summary>
    /// NUL cannot occur in a label or a URL, so it separates them without the ambiguity a space
    /// would: "a b"+"c" and "a"+"b c" stay distinct keys.
    /// </summary>
    private const string KeySeparator = "\u0000";

    [GeneratedRegex(@"\bhttps?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\bwww\.[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareWww();

    [GeneratedRegex(@"\b[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,24}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HostLike();

    [GeneratedRegex(@"^[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EmailLike();

    /// <summary>
    /// Every link in a plain text body, the entry point for a channel with no markup to read.
    /// </summary>
    /// <remarks>
    /// A bare URL is its own label, so no mismatch can be reported against it. That is not a gap:
    /// there is nothing in the message for it to have disagreed with, and inventing a claim here
    /// would be the analysis attributing a statement the sender never made.
    /// </remarks>
    public static IReadOnlyList<LinkFinding> FromPlainText(string plainText, int maxLinks) =>
        Extract(declaredLinks: [], plainText, maxLinks);

    /// <summary>
    /// Declared links first, then bare URLs found in the text, as one bounded and deduplicated set.
    /// </summary>
    /// <remarks>
    /// The declared links come first and the two share one budget and one deduplication set, because
    /// a link that appears both as an anchor and again as visible text is one link, and because a
    /// limit applied to each source separately is a limit that does not hold.
    /// </remarks>
    public static IReadOnlyList<LinkFinding> Extract(
        IEnumerable<(string Href, string Label)> declaredLinks,
        string plainText,
        int maxLinks)
    {
        ArgumentNullException.ThrowIfNull(declaredLinks);

        var findings = new List<LinkFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (href, label) in declaredLinks)
        {
            if (findings.Count >= maxLinks)
            {
                break;
            }

            // A blank href costs nothing, so it is skipped without consuming any of the budget.
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            Add(href, label);
        }

        foreach (var match in BareUrlsIn(plainText))
        {
            if (findings.Count >= maxLinks)
            {
                break;
            }

            Add(match, match);
        }

        return findings;

        void Add(string href, string label)
        {
            var target = UrlTools.Observe(href);
            var trimmedLabel = (label ?? string.Empty).Trim();
            var key = string.Concat(trimmedLabel, KeySeparator, href);
            if (!seen.Add(key))
            {
                return;
            }

            var (mismatch, kind, claimsHost) = Compare(trimmedLabel, target);

            findings.Add(new LinkFinding
            {
                DisplayedText = trimmedLabel,
                ActualTarget = href,
                Target = target,
                Idn = target is null ? null : UrlTools.InspectIdn(target),
                DisplayMismatch = mismatch,
                MismatchKind = kind,
                LabelMakesHostClaim = claimsHost,
            });
        }
    }

    /// <summary>URL-shaped text found in a body, in the order it appears.</summary>
    public static IEnumerable<string> BareUrlsIn(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in BareUrl().Matches(text))
        {
            yield return match.Value;
        }

        foreach (Match match in BareWww().Matches(text))
        {
            yield return match.Value;
        }
    }

    /// <summary>
    /// Compares a link's visible label with the host it actually points at.
    /// </summary>
    /// <remarks>
    /// Only a label that itself makes a host-shaped claim is compared. Labels like "click here" or
    /// "view your invoice" name no destination, so there is nothing to disagree with, treating
    /// those as mismatches would bury the real ones.
    /// </remarks>
    private static (bool Mismatch, string? Kind, bool ClaimsHost) Compare(string label, UrlObservation? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(label))
        {
            return (false, null, false);
        }

        var targetHost = target.Host.TrimEnd('.');

        // A label that is itself a URL.
        if (label.Contains("://", StringComparison.Ordinal) ||
            label.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            var labelUrl = UrlTools.Observe(label);
            if (labelUrl is not null)
            {
                if (!string.Equals(TrimWww(labelUrl.Host), TrimWww(targetHost), StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "label-host", true);
                }

                if (!labelUrl.IsPlaintext && target.IsPlaintext)
                {
                    return (true, "label-scheme", true);
                }
            }

            return (false, null, true);
        }

        // A label that is an email address.
        if (EmailLike().IsMatch(label))
        {
            var at = label.IndexOf('@');
            var labelDomain = label[(at + 1)..];
            return (
                !string.Equals(TrimWww(labelDomain), TrimWww(targetHost), StringComparison.OrdinalIgnoreCase),
                "label-email",
                true);
        }

        // A label containing a host-shaped word.
        var hostMatch = HostLike().Match(label);
        if (!hostMatch.Success)
        {
            return (false, null, false);
        }

        return (
            !string.Equals(TrimWww(hostMatch.Value), TrimWww(targetHost), StringComparison.OrdinalIgnoreCase),
            "label-host",
            true);
    }

    private static string TrimWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
