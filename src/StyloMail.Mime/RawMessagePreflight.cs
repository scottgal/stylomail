using System.Text;
using System.Text.RegularExpressions;

namespace StyloMail.Mime;

/// <summary>What a bounded pre-scan of the raw bytes found, before any real parsing happens.</summary>
internal sealed record PreflightResult
{
    /// <summary>Offset just past the header/body separator, or the end of input for a headers-only message.</summary>
    public required int HeaderBlockEnd { get; init; }

    public required int HeaderCount { get; init; }

    public required long HeaderBytes { get; init; }

    public required int MaxHeaderLineLength { get; init; }

    /// <summary>Declared multipart boundary tokens, in the order encountered and capped.</summary>
    public required IReadOnlyList<string> Boundaries { get; init; }

    /// <summary>A boundary that opened parts but was never closed — the message was cut short.</summary>
    public required bool BoundaryUnterminated { get; init; }

    public required int OpeningDelimiterCount { get; init; }

    /// <summary>Set when a structural limit was breached, so parsing is refused outright.</summary>
    public MimeParseRejection? Rejection { get; init; }
}

/// <summary>
/// A cheap, bounded pass over the raw bytes that runs <em>before</em> the real parser.
/// </summary>
/// <remarks>
/// The real parser is a mature one, but it is not a bouncer: it will happily spend the afternoon
/// unfolding a million headers. Everything here is O(n) over at most
/// <see cref="MimeParseLimits.MaxMessageBytes"/> with early exit, so a message that is structurally
/// hostile is refused before anyone commits memory to it.
///
/// <para>
/// It also decides whether the input is a message at all. A file with no header/body separator and
/// no header-shaped opening lines is not a message, and reporting it as one would mean assessing a
/// fragment.
/// </para>
/// </remarks>
internal static partial class RawMessagePreflight
{
    [GeneratedRegex("""boundary\s*=\s*(?:"([^"]{1,200})"|([^\s;"]{1,200}))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryParameter();

    public static PreflightResult Scan(ReadOnlySpan<byte> raw, MimeParseLimits limits)
    {
        if (raw.IsEmpty)
        {
            return Refuse(MimeParseDisposition.Malformed, "empty-input", limits, observed: 0, limit: null);
        }

        var scanLimit = (int)Math.Min(raw.Length, limits.MaxHeaderBytes);
        var separator = FindHeaderBodySeparator(raw, scanLimit);

        if (separator is null)
        {
            // No separator inside the header budget. Either the header block is far too large, or
            // this was never a message. A header-only message (headers, no blank line, no body) is
            // still a message and is accepted here with an empty body.
            if (raw.Length > limits.MaxHeaderBytes && LooksLikeHeaderStart(raw))
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "header-bytes", limits,
                    observed: raw.Length, limit: limits.MaxHeaderBytes);
            }

            if (!LooksLikeWholeHeaderBlock(raw, scanLimit, out var headerOnly))
            {
                return Refuse(MimeParseDisposition.Malformed, "no-header-body-separator", limits,
                    observed: raw.Length, limit: null);
            }

            return FinishPreflight(
                raw,
                limits,
                headerBlockEnd: scanLimit,
                headerCount: headerOnly.Count,
                headerBytes: headerOnly.Bytes,
                maxHeaderLineLength: headerOnly.MaxLineLength,
                validFieldLines: headerOnly.ValidFieldLines);
        }

        var headerEnd = separator.Value.End;
        var headerScan = ScanHeaders(raw, 0, separator.Value.Start);

        return FinishPreflight(
            raw,
            limits,
            headerBlockEnd: headerEnd,
            headerCount: headerScan.Count,
            headerBytes: headerScan.Bytes,
            maxHeaderLineLength: headerScan.MaxLineLength,
            validFieldLines: headerScan.ValidFieldLines);

        static PreflightResult FinishPreflight(
            ReadOnlySpan<byte> raw,
            MimeParseLimits limits,
            int headerBlockEnd,
            int headerCount,
            long headerBytes,
            int maxHeaderLineLength,
            int validFieldLines)
        {
            if (validFieldLines == 0)
            {
                return Refuse(MimeParseDisposition.Malformed, "no-header-fields", limits,
                    observed: raw.Length, limit: null);
            }

            if (headerCount > limits.MaxHeaderCount)
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "header-count", limits,
                    headerCount, limits.MaxHeaderCount);
            }

            if (headerBytes > limits.MaxHeaderBytes)
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "header-bytes", limits,
                    headerBytes, limits.MaxHeaderBytes);
            }

            if (maxHeaderLineLength > limits.MaxHeaderLineLength)
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "header-line-length", limits,
                    maxHeaderLineLength, limits.MaxHeaderLineLength);
            }

            var structure = ScanStructure(raw, limits.MaxParts);

            if (structure.PartCount is not null)
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "part-count", limits,
                    structure.PartCount.Value, limits.MaxParts);
            }

            // Nesting depth is checked here, before the real parser runs. Leaving it to the parser
            // would mean a message could be silently truncated at the parser's own depth ceiling
            // and the readable part handed on as though it were the whole message — the one
            // outcome the specification rules out.
            if (structure.MaxDepth > limits.MaxMimeDepth)
            {
                return Refuse(MimeParseDisposition.LimitExceeded, "mime-depth", limits,
                    structure.MaxDepth, limits.MaxMimeDepth);
            }

            return new PreflightResult
            {
                HeaderBlockEnd = headerBlockEnd,
                HeaderCount = headerCount,
                HeaderBytes = headerBytes,
                MaxHeaderLineLength = maxHeaderLineLength,
                Boundaries = structure.Boundaries,
                BoundaryUnterminated = structure.Unterminated,
                OpeningDelimiterCount = structure.OpeningCount,
            };
        }

        static PreflightResult Refuse(
            MimeParseDisposition disposition,
            string reason,
            MimeParseLimits limits,
            long observed,
            long? limit)
        {
            return new PreflightResult
            {
                HeaderBlockEnd = 0,
                HeaderCount = 0,
                HeaderBytes = 0,
                MaxHeaderLineLength = 0,
                Boundaries = [],
                BoundaryUnterminated = false,
                OpeningDelimiterCount = 0,
                Rejection = new MimeParseRejection
                {
                    Disposition = disposition,
                    Reason = reason,
                    LimitName = limit is null ? null : reason,
                    Observed = observed,
                    Limit = limit,
                },
            };
        }
    }

    /// <summary>Finds the blank line that ends the header block, within the byte budget.</summary>
    private static (int Start, int End)? FindHeaderBodySeparator(ReadOnlySpan<byte> raw, int scanLimit)
    {
        for (var i = 0; i + 1 < scanLimit; i++)
        {
            if (raw[i] != (byte)'\n')
            {
                continue;
            }

            // \n\n
            if (raw[i + 1] == (byte)'\n')
            {
                return (i, i + 2);
            }

            // \n\r\n
            if (raw[i + 1] == (byte)'\r' && i + 2 < scanLimit && raw[i + 2] == (byte)'\n')
            {
                return (i, i + 3);
            }

            // \r\n\r\n
            if (raw[i + 1] == (byte)'\r' && i + 2 < scanLimit && raw[i + 2] == (byte)'\n' &&
                i + 3 < scanLimit && raw[i + 3] == (byte)'\n')
            {
                return (i + 1, i + 4);
            }
        }

        return null;
    }

    /// <summary>True when the opening bytes look like the start of a header field rather than prose.</summary>
    private static bool LooksLikeHeaderStart(ReadOnlySpan<byte> raw)
    {
        var probe = raw[..(int)Math.Min(raw.Length, 512)];
        var lineEnd = probe.IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            lineEnd = probe.Length;
        }

        var colon = probe[..lineEnd].IndexOf((byte)':');
        return colon > 0;
    }

    private static bool LooksLikeWholeHeaderBlock(ReadOnlySpan<byte> raw, int length, out HeaderScan scan)
    {
        scan = ScanHeaders(raw, 0, length);

        // Every line must be a header field or a continuation, and every field must be well formed.
        return scan.Count > 0 &&
               scan.Count == scan.ValidFieldLines &&
               scan.TotalLines == scan.Count + scan.ContinuationLines;
    }

    private readonly record struct HeaderScan(int Count, long Bytes, int MaxLineLength, int TotalLines,
        int ContinuationLines, int ValidFieldLines);

    private static HeaderScan ScanHeaders(ReadOnlySpan<byte> raw, int start, int end)
    {
        if (end <= start)
        {
            return new HeaderScan(0, 0, 0, 0, 0, 0);
        }

        var slice = raw[start..end];
        var count = 0;
        var continuations = 0;
        var totalLines = 0;
        var maxLine = 0;
        var validFields = 0;
        var lineStart = 0;
        var seenField = false;

        for (var i = 0; i <= slice.Length; i++)
        {
            var atEnd = i == slice.Length;
            if (!atEnd && slice[i] != (byte)'\n')
            {
                continue;
            }

            var lineLength = i - lineStart;
            if (lineLength > 0 && slice[i - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (lineLength > 0)
            {
                totalLines++;
                if (lineLength > maxLine)
                {
                    maxLine = lineLength;
                }

                var first = slice[lineStart];
                if (first is (byte)' ' or (byte)'\t')
                {
                    continuations++;
                }
                else
                {
                    count++;
                    seenField = true;
                    if (HasFieldName(slice.Slice(lineStart, lineLength)))
                    {
                        validFields++;
                    }
                }
            }
            else if (!atEnd && seenField && i > 0)
            {
                // Blank line inside the header block: an empty field line. Counted as a line but
                // not as a field, which is what makes this usable as a validity check.
                totalLines++;
            }

            lineStart = i + 1;
        }

        return new HeaderScan(count, slice.Length, maxLine, totalLines, continuations, validFields);
    }

    private readonly record struct StructureScan(
        IReadOnlyList<string> Boundaries,
        int OpeningCount,
        bool Unterminated,
        int MaxDepth,
        int? PartCount);

    /// <summary>
    /// One bounded pass over the message that counts part delimiters and measures nesting depth.
    /// </summary>
    /// <remarks>
    /// Delimiter lines are counted against every boundary token declared <em>anywhere</em> in the
    /// message, not just those in the outer header block, because a nested multipart declares its
    /// own boundary inside a part. Depth is tracked with a token stack: opening a delimiter for a
    /// token that is already on the stack is a sibling part, not a new level, which is exactly how
    /// a repeated boundary behaves in a well-formed multipart.
    ///
    /// <para>
    /// This is a budget check rather than a parse, so it errs toward counting more. Reporting a
    /// depth of one too many refuses a message that would have been fine; reporting one too few
    /// lets a nesting bomb through, and only one of those is recoverable.
    /// </para>
    /// </remarks>
    private static StructureScan ScanStructure(ReadOnlySpan<byte> raw, int maxParts)
    {
        var boundaries = new List<string>(8);
        var stack = new List<string>(8);
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var openings = 0;
        var maxDepth = 0;

        var i = 0;
        while (i < raw.Length)
        {
            var lineEnd = raw[i..].IndexOf((byte)'\n');
            var end = lineEnd < 0 ? raw.Length : i + lineEnd + 1;
            var line = raw[i..end];
            var trimmed = TrimAscii(line);

            if (trimmed.Length > 0)
            {
                if (trimmed[0] == (byte)'-' && trimmed.Length > 1 && trimmed[1] == (byte)'-')
                {
                    var token = MatchBoundaryDelimiter(trimmed, boundaries);
                    if (token is not null)
                    {
                        var isClosing = trimmed.Length > token.Length + 4 &&
                                        trimmed[token.Length + 2] == (byte)'-' &&
                                        trimmed[token.Length + 3] == (byte)'-';

                        if (isClosing)
                        {
                            closed.Add(token);
                            var index = stack.LastIndexOf(token);
                            if (index >= 0)
                            {
                                stack.RemoveRange(index, stack.Count - index);
                            }
                        }
                        else
                        {
                            openings++;
                            if (openings > maxParts)
                            {
                                // Already over budget: the answer cannot change, so stop reading.
                                return new StructureScan(boundaries, openings, false, maxDepth, openings);
                            }

                            if (!stack.Contains(token, StringComparer.Ordinal))
                            {
                                stack.Add(token);
                                if (stack.Count > maxDepth)
                                {
                                    maxDepth = stack.Count;
                                }
                            }

                            closed.Remove(token);
                        }
                    }
                }
                else if (boundaries.Count < 64)
                {
                    CollectBoundaryParameters(trimmed, boundaries);
                }
            }

            i = end;
        }

        var unterminated = false;
        foreach (var token in stack)
        {
            if (!closed.Contains(token))
            {
                unterminated = true;
                break;
            }
        }

        return new StructureScan(boundaries, openings, unterminated, maxDepth, null);
    }

    /// <summary>Matches a delimiter line's token against the boundaries declared so far.</summary>
    private static string? MatchBoundaryDelimiter(ReadOnlySpan<byte> line, List<string> boundaries)
    {
        foreach (var token in boundaries)
        {
            if (line.Length < token.Length + 2 || !MatchesAscii(line, 2, token))
            {
                continue;
            }

            return token;
        }

        return null;
    }

    /// <summary>Pulls any <c>boundary=</c> parameters out of one line, if it looks like a header.</summary>
    private static void CollectBoundaryParameters(ReadOnlySpan<byte> line, List<string> boundaries)
    {
        // A cheap byte-level pre-filter keeps the regex off the overwhelming majority of lines.
        if (!ContainsAsciiIgnoreCase(line, "boundary"))
        {
            return;
        }

        var text = Encoding.Latin1.GetString(line);
        foreach (Match match in BoundaryParameter().Matches(text))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (value.Length is > 0 and <= 200 && !boundaries.Contains(value, StringComparer.Ordinal))
            {
                boundaries.Add(value);
            }
        }
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> line)
    {
        var start = 0;
        var end = line.Length;
        while (end > start && (line[end - 1] == (byte)'\n' || line[end - 1] == (byte)'\r'))
        {
            end--;
        }

        while (start < end && (line[start] == (byte)' ' || line[start] == (byte)'\t'))
        {
            start++;
        }

        return line[start..end];
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (ToLowerAscii(haystack[i + j]) != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static char ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (char)(value + 32) : (char)value;

    /// <summary>
    /// Whether a line begins with a syntactically plausible header field name followed by a colon.
    /// </summary>
    /// <remarks>
    /// This is what separates "a message" from "some bytes". Any two paragraphs of prose separated
    /// by a blank line would otherwise look like a header block followed by a body, and the adapter
    /// would then report on a fragment as though it were a message.
    /// </remarks>
    private static bool HasFieldName(ReadOnlySpan<byte> line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var b = line[i];
            if (b == (byte)':')
            {
                return i > 0;
            }

            if (b is (byte)' ' or (byte)'\t' || b < 0x21 || b > 0x7e)
            {
                return false;
            }
        }

        return false;
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> raw, int offset, string token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (c > 0x7f || raw[offset + i] != (byte)c)
            {
                return false;
            }
        }

        return true;
    }
}
