using System.Text.RegularExpressions;

namespace StyloMail.Mime;

/// <summary>One link, with the comparison between what it says and where it goes.</summary>
internal sealed record LinkFinding
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
/// The comparison is textual. A label reading <c>paypal.com</c> over a target of
/// <c>paypal.com.evil.example</c> is a mismatch because the two hosts differ as written, and the
/// claim about intent stays with policy. What matters is that the label and the target are captured
/// separately and never collapsed: a great many detections are lost by storing only one of them.
/// </remarks>
internal static partial class LinkExtractor
{
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

    public static IReadOnlyList<LinkFinding> Extract(
        HtmlAnalysis html,
        string plainBody,
        MimeParseLimits limits)
    {
        // NUL cannot occur in a label or a URL, so it separates them without the
        // ambiguity a space would: "a b"+"c" and "a"+"b c" stay distinct keys.
        const string KeySeparator = "\u0000";

        var findings = new List<LinkFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in html.Links)
        {
            if (findings.Count >= limits.MaxLinks)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(link.Href))
            {
                continue;
            }

            Add(link.Href, link.Label);
        }

        foreach (var match in EnumerateBareUrls(plainBody))
        {
            if (findings.Count >= limits.MaxLinks)
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

    private static IEnumerable<string> EnumerateBareUrls(string text)
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
