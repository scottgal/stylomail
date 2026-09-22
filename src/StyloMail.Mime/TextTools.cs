using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StyloMail.Mime;

/// <summary>
/// Text normalisation, tokenisation and fingerprints shared by the deterministic extractors.
/// </summary>
/// <remarks>
/// Two properties matter here. First, everything is bounded: every pattern runs over a body that
/// has already been capped by <see cref="MimeParseLimits.MaxBodyChars"/>, and no helper allocates
/// in proportion to anything but the input it was handed.
///
/// <para>
/// Second, the fingerprints are <em>stable</em> across runs and deploys. Template similarity
/// compares a message seen today with one seen a week ago, so a hash that drifted with the process
/// would silently break campaign grouping while still looking like it worked.
/// </para>
/// </remarks>
internal static partial class TextTools
{
    [GeneratedRegex(@"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();

    /// <summary>Zero-width, bidi-override, soft-hyphen and other invisible formatting characters.</summary>
    [GeneratedRegex("[\\u200B-\\u200F\\u202A-\\u202E\\u2060-\\u2064\\u206A-\\u206F\\uFEFF\\u00AD]",
        RegexOptions.CultureInvariant)]
    private static partial Regex InvisibleCharacter();

    [GeneratedRegex("[ \\t\\u00A0\\u2000-\\u200A\\u3000]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongWhitespaceRun();

    [GeneratedRegex(@"[!?.,;:*_\-=~]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedPunctuationRun();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailLike();

    [GeneratedRegex(@"\bhttps?://\S+|\bwww\.\S+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlLike();

    /// <summary>Long hex runs and UUIDs, message ids, tracking ids, tokens.</summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{16,}\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierLike();

    /// <summary>
    /// Numbers of three digits or more, with any separators, amounts, order numbers, identifiers,
    /// years. Short numbers are left alone: "2 items" and "2pm" are content, not fillers.
    /// </summary>
    [GeneratedRegex(@"\d{3,}(?:[.,:\/\-]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    /// <summary>Lower-cases invariant and collapses all whitespace to single spaces.</summary>
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> Tokenize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var matches = WordToken().Matches(value);
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            tokens.Add(match.Value.ToLowerInvariant());
        }

        return tokens;
    }

    public static HashSet<string> TokenSet(string value) => [.. Tokenize(value)];

    /// <summary>
    /// Symmetric-overlap measured against the smaller side: how much of the shorter text is
    /// covered by the longer. Symmetric Jaccard under-reports when one side is much longer, which
    /// is the common case when comparing a short plain-text part against a long rendered one.
    /// </summary>
    public static double Containment(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        var smaller = a.Count <= b.Count ? a : b;
        var larger = a.Count <= b.Count ? b : a;
        return (double)SharedTokenCount(smaller, larger) / smaller.Count;
    }

    /// <summary>Counts tokens of <paramref name="a"/> that also occur in <paramref name="b"/>.</summary>
    public static int SharedTokenCount(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        var smaller = a.Count <= b.Count ? a : b;
        var larger = a.Count <= b.Count ? b : a;

        var shared = 0;
        foreach (var token in smaller)
        {
            if (larger.Contains(token))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>Stable 64-bit FNV-1a over UTF-16 code units. Not cryptographic.</summary>
    public static ulong StableHash(ReadOnlySpan<char> value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var ch in value)
        {
            hash ^= (byte)(ch & 0xFF);
            hash *= prime;
            hash ^= (byte)(ch >> 8);
            hash *= prime;
        }

        return hash;
    }

    public static ulong StableHash(string value) => StableHash(value.AsSpan());

    /// <summary>
    /// Similarity-preserving 64-bit sketch of a token multiset. Two near-identical templates land
    /// within a small Hamming distance of each other; making that comparison belongs to the
    /// adaptive engine, which holds the other messages.
    /// </summary>
    public static ulong SimHash(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0UL;
        }

        var weights = new int[64];
        foreach (var token in tokens)
        {
            var hash = StableHash(token);
            for (var bit = 0; bit < 64; bit++)
            {
                weights[bit] += (hash & (1UL << bit)) != 0 ? 1 : -1;
            }
        }

        var result = 0UL;
        for (var bit = 0; bit < 64; bit++)
        {
            if (weights[bit] > 0)
            {
                result |= 1UL << bit;
            }
        }

        return result;
    }

    /// <summary>
    /// A template skeleton: the text with the parts that vary between two sendings of one template
    /// replaced by placeholders, addresses, URLs, identifiers, numbers and timestamps. Two
    /// messages rendered from one template collapse to the same skeleton.
    /// </summary>
    public static string Skeleton(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var work = EmailLike().Replace(value, " email ");
        work = UrlLike().Replace(work, " url ");
        work = IdentifierLike().Replace(work, " id ");
        work = NumberLike().Replace(work, " num ");
        return Normalize(work);
    }

    /// <summary>Hexadecimal rendering of a 64-bit value, always 16 characters wide.</summary>
    public static string ToHex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    public static int CountInvisible(string value) =>
        string.IsNullOrEmpty(value) ? 0 : InvisibleCharacter().Matches(value).Count;

    public static bool HasLongWhitespaceRun(string value) =>
        !string.IsNullOrEmpty(value) && LongWhitespaceRun().IsMatch(value);

    public static int CountRepeatedPunctuationRuns(string value) =>
        string.IsNullOrEmpty(value) ? 0 : RepeatedPunctuationRun().Matches(value).Count;
}
