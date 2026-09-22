using System.Globalization;
using System.Net;
using System.Text;

namespace StyloMail.Mime;

/// <summary>The parts of a URL that deterministic analysis actually uses.</summary>
internal sealed record UrlObservation
{
    /// <summary>Host as written, lower-cased, with any punycode left as-is.</summary>
    public required string Host { get; init; }

    /// <summary>Host with IDN labels converted to Unicode, when that differs from <see cref="Host"/>.</summary>
    public string? UnicodeHost { get; init; }

    /// <summary>Host as transmitted (punycode), recorded so a homograph comparison has both forms.</summary>
    public string? AsciiHost { get; init; }

    public required bool IsIpLiteral { get; init; }

    public required bool HasUserInfo { get; init; }

    public required bool HasExplicitPort { get; init; }

    public required bool IsPlaintext { get; init; }
}

/// <summary>Why an internationalised host looks like a deliberate confusable, when it does.</summary>
internal sealed record IdnObservation
{
    public required string AsciiHost { get; init; }

    public required string UnicodeHost { get; init; }

    public required IReadOnlyList<string> Scripts { get; init; }

    public required bool IsMixedScript { get; init; }

    /// <summary>Characters that render like an ASCII letter but are not one, mapped to what they mimic.</summary>
    public required IReadOnlyList<string> Confusables { get; init; }

    /// <summary>The host with confusables folded to ASCII, what the host appears to say to a reader.</summary>
    public required string AsciiSkeleton { get; init; }
}

/// <summary>
/// URL and internationalised-domain inspection, with no network involvement whatsoever.
/// </summary>
/// <remarks>
/// Nothing here resolves, normalises via DNS or fetches anything. Punycode conversion is pure
/// string work through <see cref="IdnMapping"/>, and a host that fails to convert is reported as
/// un-convertible rather than guessed at.
///
/// <para>
/// The homograph check is a heuristic and is described as one: it finds characters that render
/// like Latin letters while belonging to another script, which is the shape of a visual spoof. It
/// does not claim to know what the sender intended, and its output feeds evidence rather than a
/// verdict.
/// </para>
/// </remarks>
internal static class UrlTools
{
    /// <summary>
    /// Characters that render near-identically to an ASCII character. Bounded and deliberately
    /// small: the common Cyrillic, Greek and fullwidth cases, not an exhaustive Unicode table.
    /// </summary>
    private static readonly Dictionary<char, char> ConfusableMap = BuildConfusables();

    private static Dictionary<char, char> BuildConfusables()
    {
        // (lookalike, what it renders as). Written as escapes rather than literals so the table can
        // be reviewed and diffed without depending on how a font renders the file.
        var pairs = new (char Lookalike, char Ascii)[]
        {
            // Cyrillic
            ('а', 'a'), ('е', 'e'), ('с', 'c'), ('о', 'o'), ('р', 'p'),
            ('х', 'x'), ('у', 'y'), ('і', 'i'), ('ѕ', 's'), ('ј', 'j'),
            ('ԁ', 'd'), ('һ', 'h'), ('ӏ', 'l'), ('м', 'm'), ('т', 't'),
            ('п', 'n'), ('к', 'k'), ('в', 'b'), ('н', 'h'), ('г', 'r'),
            ('з', '3'), ('ё', 'e'), ('ь', 'b'), ('љ', 'n'),
            // Greek
            ('ο', 'o'), ('α', 'a'), ('ρ', 'p'), ('ε', 'e'), ('ι', 'i'),
            ('ν', 'v'), ('τ', 't'), ('υ', 'u'), ('κ', 'k'), ('μ', 'm'),
            ('χ', 'x'), ('η', 'n'), ('σ', 'o'), ('ζ', 'z'),
        };

        var map = new Dictionary<char, char>(pairs.Length + 26);
        foreach (var (lookalike, ascii) in pairs)
        {
            map[lookalike] = ascii;
        }

        // Fullwidth Latin letters.
        for (var i = 0; i < 26; i++)
        {
            map[(char)(0xFF41 + i)] = (char)('a' + i);
        }

        return map;
    }

    /// <summary>
    /// Splits a URL into the parts worth recording. Returns null when there is no usable host, so
    /// a caller never gets a half-parsed URL it might mistake for a parsed one.
    /// </summary>
    public static UrlObservation? Observe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var raw = url.Trim();
        if (raw.Length > 4096)
        {
            raw = raw[..4096];
        }

        var rest = raw;
        var scheme = string.Empty;

        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0 && IsScheme(rest.AsSpan(0, schemeEnd)))
        {
            scheme = rest[..schemeEnd].ToLowerInvariant();
            rest = rest[(schemeEnd + 3)..];
        }
        else if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        var authorityEnd = rest.IndexOfAny('/', '?', '#');
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];

        var hasUserInfo = false;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            hasUserInfo = true;
            authority = authority[(at + 1)..];
        }

        var hasExplicitPort = false;
        string host;
        if (authority.StartsWith('['))
        {
            // IPv6 literal.
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                return null;
            }

            host = authority[..(close + 1)];
            hasExplicitPort = authority.Length > close + 2 && authority[close + 1] == ':';
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                hasExplicitPort = true;
                authority = authority[..colon];
            }

            host = authority;
        }

        host = host.Trim().TrimEnd('.');
        if (host.Length == 0)
        {
            return null;
        }

        var lowerHost = host.ToLowerInvariant();
        var isIpLiteral = IPAddress.TryParse(lowerHost.Trim('[', ']'), out _);

        var asciiHost = isIpLiteral ? null : TryToAscii(lowerHost);
        var unicodeHost = TryToUnicode(lowerHost);

        return new UrlObservation
        {
            Host = lowerHost,
            UnicodeHost = unicodeHost is not null && !string.Equals(unicodeHost, lowerHost, StringComparison.Ordinal)
                ? unicodeHost
                : null,
            AsciiHost = asciiHost is not null && !string.Equals(asciiHost, lowerHost, StringComparison.Ordinal)
                ? asciiHost
                : null,
            IsIpLiteral = isIpLiteral,
            HasUserInfo = hasUserInfo,
            HasExplicitPort = hasExplicitPort,
            IsPlaintext = scheme.Length == 0 || string.Equals(scheme, "http", StringComparison.Ordinal),
        };
    }

    /// <summary>Describes an internationalised host, or returns null when the host is plain ASCII.</summary>
    public static IdnObservation? InspectIdn(UrlObservation url)
    {
        var ascii = url.AsciiHost ?? url.Host;
        var unicode = url.UnicodeHost ?? url.Host;

        var isIdn = ascii.Contains("xn--", StringComparison.OrdinalIgnoreCase) ||
                    unicode.Any(c => c > 0x7f);

        if (!isIdn || url.IsIpLiteral)
        {
            return null;
        }

        var scripts = new SortedSet<string>(StringComparer.Ordinal);
        var confusables = new List<string>();
        var skeleton = new StringBuilder(unicode.Length);

        foreach (var ch in unicode)
        {
            if (ch > 0x7f)
            {
                var script = ScriptOf(ch);
                if (script is not null)
                {
                    scripts.Add(script);
                }
            }

            if (ConfusableMap.TryGetValue(ch, out var ascii2))
            {
                confusables.Add($"U+{(int)ch:X4}->{ascii2}");
                skeleton.Append(ascii2);
            }
            else
            {
                skeleton.Append(char.ToLowerInvariant(ch));
            }
        }

        var labels = unicode.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var mixed = labels.Any(HasMixedScripts);

        return new IdnObservation
        {
            AsciiHost = ascii,
            UnicodeHost = unicode,
            Scripts = [.. scripts],
            IsMixedScript = mixed,
            Confusables = confusables,
            AsciiSkeleton = skeleton.ToString(),
        };
    }

    private static bool HasMixedScripts(string label)
    {
        string? seen = null;
        foreach (var ch in label)
        {
            if (ch <= 0x7f)
            {
                continue;
            }

            var script = ScriptOf(ch);
            if (script is null)
            {
                continue;
            }

            if (seen is null)
            {
                seen = script;
            }
            else if (!string.Equals(seen, script, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ScriptOf(char ch) => ch switch
    {
        >= '\u0041' and <= '\u024F' => "latin",
        >= '\u0370' and <= '\u03FF' => "greek",
        >= '\u0400' and <= '\u052F' => "cyrillic",
        >= '\u0590' and <= '\u05FF' => "hebrew",
        >= '\u0600' and <= '\u06FF' => "arabic",
        >= '\u0900' and <= '\u097F' => "devanagari",
        >= '\u0E00' and <= '\u0E7F' => "thai",
        >= '\u3040' and <= '\u309F' => "hiragana",
        >= '\u30A0' and <= '\u30FF' => "katakana",
        >= '\u4E00' and <= '\u9FFF' => "han",
        >= '\uAC00' and <= '\uD7AF' => "hangul",
        >= '\uFF00' and <= '\uFFEF' => "fullwidth",
        _ => null,
    };

    /// <summary>The registrable-ish suffix: the last two labels. Used only to compare host families.</summary>
    public static string DomainFamily(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length switch
        {
            0 => string.Empty,
            1 => labels[0],
            _ => string.Concat(labels[^2], ".", labels[^1]),
        };
    }

    /// <summary>
    /// A domain in its ASCII (punycode) form, lower-cased.
    /// </summary>
    /// <remarks>
    /// Addresses are compared in this form because the same identity travels in two encodings:
    /// an SMTP envelope carries <c>xn--pypal-4ve.com</c> while a header may carry the Unicode
    /// <c>pаypal.com</c>. Comparing them as written would report a mismatch on every
    /// internationalised domain, which is a false positive about encoding rather than about
    /// identity, and false positives are how a signal gets turned off.
    /// </remarks>
    public static string ToAsciiDomain(string? domain)
    {
        if (string.IsNullOrEmpty(domain))
        {
            return string.Empty;
        }

        var lowered = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (lowered.All(c => c <= 0x7f))
        {
            return lowered;
        }

        return TryToAscii(lowered) ?? lowered;
    }

    /// <summary>Normalises an email address for comparison: lower-cased, with an ASCII domain.</summary>
    public static string NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var trimmed = address.Trim();
        var at = trimmed.LastIndexOf('@');
        return at < 0
            ? trimmed.ToLowerInvariant()
            : string.Concat(trimmed[..at].ToLowerInvariant(), "@", ToAsciiDomain(trimmed[(at + 1)..]));
    }

    private static string? TryToAscii(string host)
    {
        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TryToUnicode(string host)
    {
        try
        {
            return new IdnMapping().GetUnicode(host);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
