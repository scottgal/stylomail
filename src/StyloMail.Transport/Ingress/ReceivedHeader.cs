using System.Globalization;
using System.Text;

namespace StyloMail.Transport.Ingress;

/// <summary>What a hop marker should say.</summary>
public sealed record ReceivedHeaderStamp
{
    /// <summary>The name this system is known by, the <c>by</c> clause, and the anchor for loop detection.</summary>
    public required string ByHost { get; init; }

    /// <summary>
    /// The name the sending client announced. <b>Untrusted</b>, a client may claim anything.
    /// </summary>
    public string? FromHost { get; init; }

    /// <summary>The connecting address, when there is one. Absent for a connector with no connection.</summary>
    public string? FromAddress { get; init; }

    /// <summary>The protocol the message arrived over: <c>ESMTP</c>, <c>HTTPS</c>.</summary>
    public required string Protocol { get; init; }

    /// <summary>A per-hop identifier, for correlating this line with the ledger.</summary>
    public required string HopId { get; init; }

    /// <summary>Injected clock. Nothing here reads the wall clock directly.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Builds and prepends the trace line that records StyloMail's own hop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a proxy has to add one.</b> RFC 5321 requires every relay to record its hop, and the
/// operational reason is sharper than the compliance one: <b>a relay that does not mark its own hop
/// cannot detect itself in a loop.</b> The inbound loop guard looks for a <c>Received</c> line whose
/// <c>by</c> clause names us, and if we never write one, our hop is invisible to it, leaving only
/// the hop-limit backstop. The mechanism would have been checking for evidence we never produced.
/// </para>
/// <para>
/// <b>This does not license rewriting anything else.</b> Byte preservation exists for
/// <em>signature integrity</em>, not byte-identity for its own sake, and prepending one line is
/// compatible with both: a DKIM signature covers only the headers named in its <c>h=</c> tag, and
/// <c>Received</c> is not among them, so a signature survives a prepend that would not survive a
/// reorder of the headers it actually signed. Exactly one line is prepended; the body and every
/// existing header are untouched and in order. The test suite pins that as a property rather than a
/// comment.
/// </para>
/// <para>
/// <b>The value is assembled from untrusted input</b>, a client's announced name, an envelope
/// address, a connector-supplied recipient. Every interpolated token is reduced to a character set
/// that cannot contain CR, LF, space, <c>(</c>, <c>)</c>, <c>;</c>, <c>&lt;</c> or <c>&gt;</c>, which
/// is exactly the set needed to forge a header or a clause. Sanitising after interpolation would be
/// the wrong order; the token set is the control, and it is applied to every part.
/// </para>
/// </remarks>
public static class ReceivedHeader
{
    /// <summary>Longest value we will emit, so a hostile token cannot produce an unbounded line.</summary>
    public const int MaxValueLength = 512;

    private const int MaxTokenLength = 120;

    /// <summary>Characters permitted inside an interpolated token.</summary>
    /// <remarks>
    /// Hostnames, addresses and IPv6 literals need nothing outside this set. Everything excluded is
    /// excluded because it is structural: whitespace separates clauses, <c>;</c> ends the value,
    /// <c>(</c>/<c>)</c> open a comment, <c>&lt;</c>/<c>&gt;</c> delimit a route, and CR/LF end the
    /// header. A token drawn from this set cannot express any of those.
    /// </remarks>
    private const string TokenAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-@:[]";

    /// <summary>Builds the complete header line, including the <c>Received: </c> prefix.</summary>
    public static string Build(ReceivedHeaderStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);

        var by = Token(stamp.ByHost);
        var protocol = Token(stamp.Protocol);
        var hopId = Token(stamp.HopId);

        var builder = new StringBuilder("Received: ");

        if (stamp.FromHost is { Length: > 0 } fromHost)
        {
            builder.Append("from ").Append(Token(fromHost));

            if (stamp.FromAddress is { Length: > 0 } fromAddress)
            {
                // The address goes in a comment, which is the conventional place for it.
                builder.Append(" (").Append(Token(fromAddress)).Append(')');
            }

            builder.Append(' ');
        }

        builder.Append("by ").Append(by)
            .Append(" with ").Append(protocol)
            .Append(" id ").Append(hopId)
            .Append("; ").Append(FormatDate(stamp.At));

        var value = builder.ToString();
        return value.Length <= MaxValueLength ? value : value[..MaxValueLength];
    }

    /// <summary>
    /// Prepends a header line to a message, preserving every existing byte.
    /// </summary>
    /// <remarks>
    /// The separator is omitted deliberately. A multi-recipient message must not record one
    /// recipient in its own trace (that is a <c>Bcc</c> leak into the stored artefact), and the
    /// clause is optional in the grammar.
    /// </remarks>
    public static byte[] Prepend(ReadOnlySpan<byte> message, string headerLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerLine);

        var prefix = Encoding.ASCII.GetBytes(string.Concat(headerLine, "\r\n"));
        var result = new byte[prefix.Length + message.Length];

        prefix.CopyTo(result, 0);
        message.CopyTo(result.AsSpan(prefix.Length));

        return result;
    }

    /// <summary>
    /// Reduces one interpolated value to the permitted alphabet.
    /// </summary>
    /// <remarks>
    /// Characters outside the set become <c>?</c> rather than being dropped, so a forged value
    /// leaves a visible trace instead of silently becoming a well-formed lie. An empty result
    /// becomes <c>unknown</c>, a blank clause would be a syntax error in the value.
    /// </remarks>
    internal static string Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxTokenLength));

        foreach (var c in value)
        {
            if (builder.Length >= MaxTokenLength)
            {
                break;
            }

            builder.Append(TokenAlphabet.Contains(c, StringComparison.Ordinal) ? c : '?');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    /// <summary>
    /// Formats an RFC 5322 date with a numeric zone.
    /// </summary>
    /// <remarks>
    /// Invariant culture explicitly: the day and month names are part of the wire format, and a
    /// machine with a non-English locale would otherwise emit a header no other MTA could parse.
    /// The zone is rendered as <c>+0000</c> rather than <c>+00:00</c>, which is what the grammar
    /// specifies and what every other implementation expects.
    /// </remarks>
    private static string FormatDate(DateTimeOffset at)
    {
        var offset = at.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();

        var zone = string.Concat(
            sign.ToString(CultureInfo.InvariantCulture),
            magnitude.Hours.ToString("D2", CultureInfo.InvariantCulture),
            magnitude.Minutes.ToString("D2", CultureInfo.InvariantCulture));

        return string.Concat(
            at.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            " ",
            zone);
    }
}
