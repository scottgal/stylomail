using System.Text;

namespace StyloMail.Transport.Ingress;

/// <summary>Bounds for the transport's own header pre-scan.</summary>
public sealed record TransportHeaderLimits
{
    /// <summary>
    /// Bytes of header block to look at before giving up.
    /// </summary>
    /// <remarks>
    /// The scan runs before anything else, on bytes the sender controls, so it needs its own ceiling.
    /// A message with a megabyte of headers is not going to be delivered anywhere, and reading it to
    /// find that out is the denial of service.
    /// </remarks>
    public int MaxHeaderBytes { get; init; } = 256 * 1024;

    /// <summary>Header fields to read before giving up.</summary>
    public int MaxHeaderCount { get; init; } = 500;

    /// <summary>Length of a single header line, before unfolding.</summary>
    public int MaxHeaderLineBytes { get; init; } = 8192;
}

/// <summary>
/// What the transport learned from a message's headers before handing it on.
/// </summary>
/// <remarks>
/// Deliberately not the MIME adapter's analysis view. That view is for <em>assessment</em>, what the
/// message says and whether it is suspicious. This is for <em>transport correctness</em>, how many
/// hops it has taken, whether it has already been through us, and whether its headers are within
/// budget at all. Conflating the two would make the hop limit depend on whether parsing succeeded,
/// and a malformed message is exactly when you most want the loop guard to still work.
/// </remarks>
public sealed record TransportHeaderFacts
{
    /// <summary>
    /// Hops the message has already taken, counted from its own <c>Received</c> headers.
    /// </summary>
    /// <remarks>
    /// A count of what the message <em>claims</em>, which is why it is a bound and not a fact: a
    /// sender can omit or forge <c>Received</c> headers. Stripping them is the classic way to defeat
    /// a hop limit, which is why the count is one guard and not the only one.
    /// </remarks>
    public required int ReceivedCount { get; init; }

    /// <summary>True when a <c>Received</c> header names this system as the receiving host.</summary>
    public required bool LoopDetected { get; init; }

    /// <summary>The <c>Received</c> value that named us, for the ledger.</summary>
    public string? LoopEvidence { get; init; }

    /// <summary>The message's own <c>Message-ID</c>. <b>Untrusted</b>, recorded, never a key.</summary>
    public string? UntrustedMessageId { get; init; }

    /// <summary>Header fields seen.</summary>
    public required int HeaderCount { get; init; }

    /// <summary>Bytes of header block read.</summary>
    public required long HeaderBytes { get; init; }

    /// <summary>
    /// Non-null when a header bound was breached, naming the bound.
    /// </summary>
    /// <remarks>
    /// A message whose headers exceed the budget is refused rather than partly read. Scanning a
    /// prefix and reporting it as the message's hops would be worse than refusing: it would produce
    /// a confident-looking number from incomplete data, and the hop limit is a safety guard.
    /// </remarks>
    public string? BoundExceeded { get; init; }
}

/// <summary>
/// A bounded, allocation-light pass over a message's header block.
/// </summary>
/// <remarks>
/// Runs on the raw bytes before any MIME parsing, and never throws for hostile input. Hop limits and
/// loop detection are transport guards that must work on a message too malformed to analyse, a
/// message designed to break the parser is precisely the one you do not want looping.
/// </remarks>
public static class TransportHeaderScanner
{
    /// <summary>Scans the header block of <paramref name="raw"/>.</summary>
    /// <param name="raw">The message bytes, as received.</param>
    /// <param name="limits">Header bounds.</param>
    /// <param name="localIdentities">
    /// Names this system is known by. A <c>Received</c> header whose <c>by</c> clause names one of
    /// them means the message has already been through us.
    /// </param>
    public static TransportHeaderFacts Scan(
        ReadOnlySpan<byte> raw,
        TransportHeaderLimits limits,
        IReadOnlyCollection<string> localIdentities)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(localIdentities);

        var received = 0;
        var headerCount = 0;
        long headerBytes = 0;
        string? messageId = null;
        string? loopEvidence = null;
        string? boundExceeded = null;

        var position = 0;
        var name = new StringBuilder();
        var value = new StringBuilder();
        var inHeader = false;

        while (position < raw.Length)
        {
            var line = ReadLine(raw, ref position);

            if (line.Length == 0)
            {
                // The blank line ends the header block. Anything after it is body.
                break;
            }

            if (line.Length > limits.MaxHeaderLineBytes)
            {
                boundExceeded = "header-line-bytes";
                break;
            }

            headerBytes += line.Length + 2;

            if (headerBytes > limits.MaxHeaderBytes)
            {
                boundExceeded = "header-bytes";
                break;
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                // A folded continuation belongs to the header above it. Unfolded with a single space,
                // which is what RFC 5322 says the fold represents.
                if (inHeader)
                {
                    value.Append(' ').Append(Encoding.UTF8.GetString(Trim(line)).Trim());
                }

                continue;
            }

            if (inHeader)
            {
                ApplyHeader(name.ToString(), value.ToString(), localIdentities, ref received, ref loopEvidence, ref messageId);
            }

            headerCount++;
            if (headerCount > limits.MaxHeaderCount)
            {
                boundExceeded = "header-count";
                break;
            }

            var colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                // Not a header field. Stop rather than guess: continuing would let a line shaped like
                // a header be counted as one.
                break;
            }

            name.Clear();
            name.Append(Encoding.UTF8.GetString(line[..colon]).Trim());

            value.Clear();
            value.Append(Encoding.UTF8.GetString(line[(colon + 1)..]).Trim());

            inHeader = true;
        }

        if (boundExceeded is null && inHeader)
        {
            ApplyHeader(name.ToString(), value.ToString(), localIdentities, ref received, ref loopEvidence, ref messageId);
        }

        return new TransportHeaderFacts
        {
            ReceivedCount = received,
            LoopDetected = loopEvidence is not null,
            LoopEvidence = loopEvidence,
            UntrustedMessageId = messageId,
            HeaderCount = headerCount,
            HeaderBytes = headerBytes,
            BoundExceeded = boundExceeded,
        };
    }

    private static void ApplyHeader(
        string name,
        string value,
        IReadOnlyCollection<string> localIdentities,
        ref int received,
        ref string? loopEvidence,
        ref string? messageId)
    {
        if (name.Equals("Received", StringComparison.OrdinalIgnoreCase))
        {
            received++;

            if (loopEvidence is null && NamesUsAsReceiver(value, localIdentities))
            {
                loopEvidence = value;
            }

            return;
        }

        if (messageId is null && name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
        {
            messageId = value;
        }
    }

    /// <summary>
    /// Whether a <c>Received</c> value's <c>by</c> clause names one of our identities.
    /// </summary>
    /// <remarks>
    /// Only the <c>by</c> clause is examined, and that is a deliberate narrowing. Our name appearing
    /// in a <c>from</c> or <c>for</c> clause is normal, a correspondent's server will write our
    /// domain in a <c>for</c> clause on nearly every message it sends us, so matching anywhere in
    /// the value would declare a loop on ordinary inbound mail. <c>by</c> is the clause that asserts
    /// who accepted the message, and it is the one that a second pass through our own infrastructure
    /// would carry.
    ///
    /// <para>
    /// Still a heuristic, and it is a <em>second</em> guard: a sender can forge or strip
    /// <c>Received</c> lines entirely, which is why the hop limit is enforced independently rather
    /// than derived from this.
    /// </para>
    /// </remarks>
    private static bool NamesUsAsReceiver(string receivedValue, IReadOnlyCollection<string> localIdentities)
    {
        if (localIdentities.Count == 0)
        {
            return false;
        }

        var span = receivedValue.AsSpan();
        var position = 0;

        while (position < span.Length)
        {
            var token = NextToken(span, ref position);
            if (token.Length == 0)
            {
                continue;
            }

            if (!token.Equals("by", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = NextToken(span, ref position);
            if (target.Length == 0)
            {
                continue;
            }

            // A clause ends at the delimiter that follows it, so strip any trailing punctuation
            // before comparing rather than reporting a loop for "by mail.example.com;".
            var end = target.Length;
            while (end > 0 && target[end - 1] is '.' or ',' or ';')
            {
                end--;
            }

            var candidate = target[..end];

            foreach (var identity in localIdentities)
            {
                if (!string.IsNullOrWhiteSpace(identity)
                    && candidate.Equals(identity, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the next whitespace-delimited token, advancing <paramref name="position"/> past it.
    /// </summary>
    /// <remarks>
    /// Advances the caller's index rather than returning a tuple, because a
    /// <see cref="ReadOnlySpan{T}"/> of <c>char</c> cannot be a tuple element.
    /// </remarks>
    private static ReadOnlySpan<char> NextToken(ReadOnlySpan<char> span, ref int position)
    {
        while (position < span.Length
            && (char.IsWhiteSpace(span[position]) || span[position] is '(' or ')' or ';'))
        {
            position++;
        }

        var start = position;
        while (position < span.Length && !char.IsWhiteSpace(span[position]))
        {
            position++;
        }

        return span[start..position];
    }

    /// <summary>Reads one line, consuming the CRLF or LF. Returns a view without the terminator.</summary>
    private static ReadOnlySpan<byte> ReadLine(ReadOnlySpan<byte> raw, ref int position)
    {
        var start = position;
        var end = start;

        while (end < raw.Length && raw[end] != (byte)'\n')
        {
            end++;
        }

        position = end < raw.Length ? end + 1 : raw.Length;

        if (end > start && raw[end - 1] == (byte)'\r')
        {
            end--;
        }

        return raw[start..end];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;

        while (start < end && value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return value[start..end];
    }
}
