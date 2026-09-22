using StyloMail.Core;
using static StyloMail.Mime.Attr;

namespace StyloMail.Mime;

/// <summary>Obfuscation indicators found in a message, as counts rather than a score.</summary>
internal sealed record ObfuscationReport
{
    public required int IndicatorCount { get; init; }

    public required IReadOnlyList<EvidenceAttribute> Indicators { get; init; }

    /// <summary>Tokens that appear in the hidden HTML but nowhere in what the reader sees.</summary>
    public required int HiddenOnlyTokenCount { get; init; }
}

/// <summary>
/// Finds padding, hidden text and other tricks that make a message read differently to a person
/// and to a filter.
/// </summary>
/// <remarks>
/// Every indicator is reported as its own count and as its own named attribute. There is no
/// combined "obfuscation score" here, because a score would be a judgement and judgements belong
/// to policy where they can be versioned and explained. One hidden span in a marketing footer is
/// not the same fact as forty thousand zero-width characters, and collapsing them into one number
/// would throw away exactly the distinction that makes the signal usable.
/// </remarks>
internal static class PaddingObfuscationScanner
{
    public static ObfuscationReport Scan(
        string plainBody,
        string visibleHtmlText,
        HtmlAnalysis html)
    {
        var indicators = new List<EvidenceAttribute>();

        var invisibleChars = TextTools.CountInvisible(plainBody) + TextTools.CountInvisible(visibleHtmlText);
        if (invisibleChars > 0)
        {
            indicators.Add(Of("invisible-characters", invisibleChars.ToString()));
        }

        var punctuationRuns = TextTools.CountRepeatedPunctuationRuns(plainBody) +
                              TextTools.CountRepeatedPunctuationRuns(visibleHtmlText);
        if (punctuationRuns > 0)
        {
            indicators.Add(Of("repeated-punctuation-runs", punctuationRuns.ToString()));
        }

        if (TextTools.HasLongWhitespaceRun(plainBody) || TextTools.HasLongWhitespaceRun(visibleHtmlText))
        {
            indicators.Add(Of("whitespace-runs", "present"));
        }

        if (html.HiddenElementCount > 0)
        {
            indicators.Add(Of("hidden-elements", html.HiddenElementCount.ToString()));
        }

        if (html.CommentBytes > 1024)
        {
            indicators.Add(Of("comment-padding-bytes", html.CommentBytes.ToString()));
        }

        var hiddenOnlyTokens = 0;
        if (html.HiddenText.Length > 0)
        {
            var visibleTokens = TextTools.TokenSet(visibleHtmlText.Length > 0 ? visibleHtmlText : plainBody);
            var hiddenTokens = TextTools.TokenSet(html.HiddenText);
            hiddenOnlyTokens = hiddenTokens.Count - TextTools.SharedTokenCount(hiddenTokens, visibleTokens);
            if (hiddenOnlyTokens > 0)
            {
                indicators.Add(Of("hidden-only-tokens", hiddenOnlyTokens.ToString()));
            }
        }

        if (html.TrackingPixelCount > 0)
        {
            indicators.Add(Of("tracking-pixels", html.TrackingPixelCount.ToString()));
        }

        return new ObfuscationReport
        {
            IndicatorCount = indicators.Count,
            Indicators = indicators,
            HiddenOnlyTokenCount = hiddenOnlyTokens,
        };
    }
}
