using System.Text;
using System.Text.RegularExpressions;

namespace StyloMail.Mime;

/// <summary>A link as it appears in the markup, before any interpretation.</summary>
internal sealed record RawLink(string Href, string Label);

/// <summary>What a bounded pass over the HTML body yielded.</summary>
internal sealed record HtmlAnalysis
{
    public required string VisibleText { get; init; }

    /// <summary>
    /// Text inside elements styled or marked as not displayed. Kept separate because it is the
    /// whole point: a message can say one thing to a reader and another to a filter, and the gap
    /// between these two strings is where that shows up.
    /// </summary>
    public required string HiddenText { get; init; }

    public required int CommentBytes { get; init; }

    public required int HiddenElementCount { get; init; }

    public required IReadOnlyList<RawLink> Links { get; init; }

    public required IReadOnlyList<string> FormActions { get; init; }

    public required int PasswordInputCount { get; init; }

    public required int RemoteImageCount { get; init; }

    public required int TrackingPixelCount { get; init; }

    public static readonly HtmlAnalysis Empty = new()
    {
        VisibleText = string.Empty,
        HiddenText = string.Empty,
        CommentBytes = 0,
        HiddenElementCount = 0,
        Links = [],
        FormActions = [],
        PasswordInputCount = 0,
        RemoteImageCount = 0,
        TrackingPixelCount = 0,
    };
}

/// <summary>
/// Bounded HTML-to-text and markup observation.
/// </summary>
/// <remarks>
/// This is deliberately a scanner, not a DOM. A real HTML parser would be more faithful, but it
/// would also be a much larger surface to point at hostile input, and the signals here are
/// indicators — "the HTML and the text disagree", "there is hidden text" — that do not need DOM
/// fidelity to be useful. Every pattern is compiled with
/// <see cref="RegexOptions.NonBacktracking"/>, so a crafted body cannot put the scanner into
/// exponential backtracking, and the per-element sweeps are bounded by explicit counters.
/// </remarks>
internal static partial class HtmlExtraction
{
    /// <summary>How many hiding-styled elements are examined individually before we stop counting.</summary>
    private const int MaxHiddenElementScans = 256;

    /// <summary>How far past an opening tag to look for its closing tag.</summary>
    private const int HiddenElementSearchWindow = 8192;

    private const int MaxHiddenTextChars = 64 * 1024;
    private const int MaxLinks = 512;

    private const RegexOptions Linear = RegexOptions.IgnoreCase | RegexOptions.Singleline |
                                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    [GeneratedRegex(@"<!--.*?-->", Linear)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", Linear)]
    private static partial Regex ScriptElement();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", Linear)]
    private static partial Regex StyleElement();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h[1-6]|/td|/table|/blockquote)\b[^>]*>", Linear)]
    private static partial Regex BlockBoundaryTag();

    [GeneratedRegex(@"<[^>]*>", Linear)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<(?:a|span|div|p|td|font|h[1-6]|strong|em|b|i)\b[^>]*style\s*=\s*(?:""[^""]*""|'[^']*')[^>]*>", Linear)]
    private static partial Regex StyledElement();

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden|font-size\s*:\s*(?:0|0px|0pt|1px)|opacity\s*:\s*0(?:\.0+)?\b|width\s*:\s*0(?:px)?\b|height\s*:\s*0(?:px)?\b", Linear)]
    private static partial Regex HidingStyle();

    [GeneratedRegex(@"<(?:a|span|div|p|td)\b[^>]*\bhidden\b[^>]*>", Linear)]
    private static partial Regex HiddenAttributeElement();

    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a\s*>", Linear)]
    private static partial Regex AnchorElement();

    [GeneratedRegex(@"<form\b([^>]*)>", Linear)]
    private static partial Regex FormElement();

    [GeneratedRegex(@"<input\b[^>]*>", Linear)]
    private static partial Regex InputElement();

    [GeneratedRegex(@"<img\b([^>]*)>", Linear)]
    private static partial Regex ImageElement();

    [GeneratedRegex(@"\btype\s*=\s*(?:""\s*password\s*""|'\s*password\s*'|password\b)", Linear)]
    private static partial Regex PasswordType();

    [GeneratedRegex(@"\bwidth\s*=\s*(?:""?\s*([0-9]{1,3})\s*""?|'?\s*([0-9]{1,3})\s*'?)", Linear)]
    private static partial Regex WidthAttribute();

    [GeneratedRegex(@"\bheight\s*=\s*(?:""?\s*([0-9]{1,3})\s*""?|'?\s*([0-9]{1,3})\s*'?)", Linear)]
    private static partial Regex HeightAttribute();

    [GeneratedRegex(@"\b(?:src|href|action)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", Linear)]
    private static partial Regex UrlAttribute();

    public static HtmlAnalysis Analyze(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return HtmlAnalysis.Empty;
        }

        var working = html;

        var commentBytes = 0;
        foreach (Match match in Comment().Matches(working))
        {
            commentBytes += match.Length;
        }

        working = Comment().Replace(working, " ");
        working = ScriptElement().Replace(working, " ");
        working = StyleElement().Replace(working, " ");

        var elided = ElideHiddenElements(working);

        var links = ExtractLinks(elided.Html);
        var (formActions, passwordInputs) = ExtractForms(elided.Html);
        var (remoteImages, trackingPixels) = ExtractImages(elided.Html);

        var visible = ToText(elided.Html);

        return new HtmlAnalysis
        {
            VisibleText = visible,
            HiddenText = elided.HiddenText,
            CommentBytes = commentBytes,
            HiddenElementCount = elided.Count,
            Links = links,
            FormActions = formActions,
            PasswordInputCount = passwordInputs,
            RemoteImageCount = remoteImages,
            TrackingPixelCount = trackingPixels,
        };
    }

    /// <summary>HTML with hidden elements removed, and the text that was removed.</summary>
    private readonly record struct ElidedHtml(string Html, string HiddenText, int Count);

    /// <summary>
    /// Removes elements that are styled or marked as invisible, returning both the remaining markup
    /// and the text that was taken out of it.
    /// </summary>
    /// <remarks>
    /// Cutting the hidden text out of the visible representation is the whole point. A message that
    /// renders one thing and contains another is exactly the trick this signal exists to expose,
    /// and leaving the hidden text in place would hand the classifier the invisible instructions as
    /// though they were the body — the trick working, reported as a tidy piece of evidence.
    ///
    /// <para>
    /// Bounded on purpose: at most <see cref="MaxHiddenElementScans"/> elements are examined and the
    /// search for each closing tag is confined to a fixed window. A body with a hundred thousand
    /// hidden spans is already conclusively an obfuscation indicator, so counting past the cap would
    /// only cost time.
    /// </para>
    /// </remarks>
    private static ElidedHtml ElideHiddenElements(string html)
    {
        var ranges = new List<(int Start, int End)>();
        var texts = new List<string>();
        var scanBudget = MaxHiddenElementScans;
        var textBudget = MaxHiddenTextChars;

        foreach (var pattern in new[] { StyledElement(), HiddenAttributeElement() })
        {
            foreach (Match match in pattern.Matches(html))
            {
                if (scanBudget <= 0 || textBudget <= 0)
                {
                    break;
                }

                var tag = match.Value;
                var opening = tag.AsSpan();
                var space = opening.IndexOfAny(" \t\r\n>");
                if (space < 1)
                {
                    continue;
                }

                var name = opening[1..space];
                var hiddenByStyle = tag.Contains("style", StringComparison.OrdinalIgnoreCase) &&
                                    HidingStyle().IsMatch(tag);

                if (!hiddenByStyle && !HiddenAttributeElement().IsMatch(tag))
                {
                    continue;
                }

                scanBudget--;

                var contentStart = match.Index + match.Length;
                var close = FindClosingTag(html, name, contentStart);
                var contentEnd = close < 0
                    ? Math.Min(html.Length, contentStart + HiddenElementSearchWindow)
                    : close;
                var elementEnd = close < 0
                    ? contentEnd
                    : Math.Min(html.Length, close + ClosingTagLength(name));

                var text = ToText(html[contentStart..contentEnd]);
                if (text.Length > 0)
                {
                    texts.Add(text);
                    textBudget -= text.Length;
                }

                ranges.Add((match.Index, elementEnd));
            }
        }

        if (ranges.Count == 0)
        {
            return new ElidedHtml(html, string.Empty, 0);
        }

        // Remove from the end so earlier offsets stay valid, skipping any range already covered.
        ranges.Sort((a, b) => b.Start.CompareTo(a.Start));
        var builder = new StringBuilder(html);
        var coveredFrom = html.Length;
        foreach (var (start, end) in ranges)
        {
            if (end > coveredFrom)
            {
                continue;
            }

            builder.Remove(start, end - start);
            coveredFrom = start;
        }

        return new ElidedHtml(builder.ToString(), TextTools.Normalize(string.Join(" ", texts)), ranges.Count);
    }

    private static int ClosingTagLength(ReadOnlySpan<char> name) => name.Length + 3;

    private static int FindClosingTag(string html, ReadOnlySpan<char> name, int from)
    {
        if (from >= html.Length)
        {
            return -1;
        }

        var limit = Math.Min(html.Length, from + HiddenElementSearchWindow);
        var window = html.AsSpan(from, limit - from);
        var needle = string.Concat("</", name.ToString());
        var index = window.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? -1 : from + index;
    }

    private static IReadOnlyList<RawLink> ExtractLinks(string html)
    {
        var links = new List<RawLink>();
        foreach (Match match in AnchorElement().Matches(html))
        {
            if (links.Count >= MaxLinks)
            {
                break;
            }

            var href = FirstUrlAttribute(match.Groups[1].Value);
            var label = ToText(match.Groups[2].Value);
            links.Add(new RawLink(href ?? string.Empty, label));
        }

        return links;
    }

    private static (IReadOnlyList<string> Actions, int Passwords) ExtractForms(string html)
    {
        var actions = new List<string>();
        foreach (Match match in FormElement().Matches(html))
        {
            if (actions.Count >= 32)
            {
                break;
            }

            var action = FirstUrlAttribute(match.Groups[1].Value);
            if (!string.IsNullOrEmpty(action))
            {
                actions.Add(action);
            }
        }

        var passwords = 0;
        foreach (Match match in InputElement().Matches(html))
        {
            if (PasswordType().IsMatch(match.Value))
            {
                passwords++;
                if (passwords >= 64)
                {
                    break;
                }
            }
        }

        return (actions, passwords);
    }

    private static (int Remote, int Tracking) ExtractImages(string html)
    {
        var remote = 0;
        var tracking = 0;
        foreach (Match match in ImageElement().Matches(html))
        {
            if (remote >= 512)
            {
                break;
            }

            var src = FirstUrlAttribute(match.Groups[1].Value);
            if (string.IsNullOrEmpty(src) || !IsRemoteSource(src))
            {
                continue;
            }

            remote++;

            var width = ReadNumericAttribute(WidthAttribute(), match.Groups[1].Value);
            var height = ReadNumericAttribute(HeightAttribute(), match.Groups[1].Value);
            if (width is > 0 and <= 2 && height is > 0 and <= 2)
            {
                tracking++;
            }
        }

        return (remote, tracking);
    }

    private static int? ReadNumericAttribute(Regex pattern, string attributes)
    {
        var match = pattern.Match(attributes);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool IsRemoteSource(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Pulls the first URL-valued attribute out of a tag's attribute text.</summary>
    public static string? FirstUrlAttribute(string attributes)
    {
        var match = UrlAttribute().Match(attributes);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;

        return DecodeEntities(value).Trim();
    }

    /// <summary>Strips markup and decodes entities, yielding readable text.</summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var working = BlockBoundaryTag().Replace(html, "\n");
        working = AnyTag().Replace(working, " ");
        working = DecodeEntities(working);
        return TextTools.Normalize(working);
    }

    /// <summary>Decodes the HTML entities that appear in mail bodies; anything else is left as written.</summary>
    public static string DecodeEntities(string value)
    {
        if (value.IndexOf('&') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var ch = value[i];
            if (ch != '&')
            {
                builder.Append(ch);
                i++;
                continue;
            }

            var semicolon = value.IndexOf(';', i + 1);
            if (semicolon < 0 || semicolon - i > 12)
            {
                builder.Append(ch);
                i++;
                continue;
            }

            var entity = value[(i + 1)..semicolon];
            var decoded = DecodeEntity(entity);
            if (decoded is null)
            {
                builder.Append(ch);
                i++;
                continue;
            }

            builder.Append(decoded);
            i = semicolon + 1;
        }

        return builder.ToString();
    }

    private static string? DecodeEntity(string entity)
    {
        if (entity.Length == 0)
        {
            return null;
        }

        if (entity[0] == '#')
        {
            var isHex = entity.Length > 1 && (entity[1] is 'x' or 'X');
            var digits = isHex ? entity[2..] : entity[1..];
            if (digits.Length == 0)
            {
                return null;
            }

            var code = isHex
                ? int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : -1
                : int.TryParse(digits, out var dec) ? dec : -1;

            return code is > 0 and <= 0x10FFFF && !char.IsSurrogate((char)Math.Min(code, 0xFFFF))
                ? char.ConvertFromUtf32(code)
                : null;
        }

        return entity.ToLowerInvariant() switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            "ensp" => " ",
            "emsp" => " ",
            "thinsp" => " ",
            "zwnj" => string.Empty,
            "zwj" => string.Empty,
            "shy" => string.Empty,
            "ndash" => "-",
            "mdash" => "-",
            "lsquo" or "rsquo" => "'",
            "ldquo" or "rdquo" => "\"",
            "hellip" => "...",
            _ => null,
        };
    }
}
