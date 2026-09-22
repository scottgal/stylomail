using System.Text.RegularExpressions;

namespace StyloMail.Mime;

/// <summary>The text a message actually adds, separated from the history it carries along.</summary>
internal sealed record QuotedSplit
{
    public required string NewText { get; init; }

    public required string QuotedText { get; init; }

    public required string Marker { get; init; }
}

/// <summary>
/// Separates new text from quoted history so the two can be weighed differently.
/// </summary>
/// <remarks>
/// This matters more than it looks. A reply that quotes a large amount of earlier mail otherwise
/// makes every message in a thread resemble every other one, which is exactly the similarity a
/// campaign detector is trying to distinguish from genuine repetition. Quote attribution is done
/// conservatively: when no marker is found the whole body is treated as new text, because
/// inventing a split would invent the distinction.
/// </remarks>
internal static partial class QuotedHistory
{
    [GeneratedRegex(@"^\s*_{5,}\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OutlookSeparator();

    [GeneratedRegex(@"^\s*-{2,}\s*(?:original message|forwarded message)\s*-{2,}\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OriginalMessageSeparator();

    [GeneratedRegex(@"^\s*on .{0,200}\bwrote:\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AttributionLine();

    [GeneratedRegex(@"^\s*>{1,}\s?", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex QuotedLine();

    [GeneratedRegex(@"^\s*(from|sent|to|subject):\s.*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ForwardedHeaderLine();

    public static QuotedSplit Split(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new QuotedSplit { NewText = string.Empty, QuotedText = string.Empty, Marker = "empty" };
        }

        var cut = body.Length;
        var marker = "none";

        Consider(OutlookSeparator(), "outlook-separator", ref cut, ref marker);
        Consider(OriginalMessageSeparator(), "original-message", ref cut, ref marker);
        Consider(AttributionLine(), "attribution-line", ref cut, ref marker);

        var quoted = QuotedLine().Match(body);
        if (quoted.Success && quoted.Index < cut)
        {
            cut = quoted.Index;
            marker = "quote-prefix";
        }

        // A forwarded header block only counts when it starts a line after a blank line, so a
        // body that merely mentions a "From:" line in prose is not mistaken for a forward.
        var forwarded = ForwardedHeaderLine().Match(body);
        if (forwarded.Success && IsBlockStart(body, forwarded.Index) && forwarded.Index < cut)
        {
            cut = forwarded.Index;
            marker = "forwarded-headers";
        }

        if (cut == body.Length && marker == "none")
        {
            return new QuotedSplit { NewText = body, QuotedText = string.Empty, Marker = marker };
        }

        return new QuotedSplit
        {
            NewText = body[..cut].TrimEnd(),
            QuotedText = body[cut..].Trim(),
            Marker = marker,
        };

        void Consider(Regex pattern, string name, ref int cut, ref string marker)
        {
            var match = pattern.Match(body);
            if (match.Success && match.Index < cut && match.Index > 0)
            {
                cut = match.Index;
                marker = name;
            }
        }

        bool IsBlockStart(string text, int index)
        {
            var lineStart = index;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
            {
                lineStart--;
            }

            // Walk back over the blank line before it.
            var probe = lineStart - 1;
            if (probe < 0)
            {
                return true;
            }

            while (probe >= 0 && (text[probe] == '\r' || text[probe] == '\n'))
            {
                probe--;
            }

            return probe < 0 || text[probe] == '\n';
        }
    }
}
