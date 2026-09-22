using System.Globalization;

namespace StyloMail.Transport.Smtp;

/// <summary>One three-digit SMTP reply, reassembled from its continuation lines.</summary>
/// <remarks>
/// <b>A reply is not a line.</b> RFC 5321 allows a server to spread one reply over many
/// <c>250-</c>-prefixed lines and terminate it with a final <c>250 </c> line. Reading a single line
/// and calling it the reply would silently take the first line of a multi-line EHLO response as the
/// whole capability list, which is how a STARTTLS advertisement gets missed and a session continues
/// in plaintext.
///
/// <para>
/// The class (<see cref="Class"/>) is derived from <see cref="Code"/> rather than stored alongside
/// it, so the two cannot disagree.
/// </para>
/// </remarks>
public sealed record SmtpReply
{
    /// <summary>The three-digit status code.</summary>
    public required int Code { get; init; }

    /// <summary>The reply's text lines, in order, with the code prefix and separator removed.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// Enhanced status code from RFC 3463 (e.g. <c>5.7.1</c>), when the server supplied one.
    /// </summary>
    /// <remarks>
    /// Recorded because it distinguishes "rejected: relay not permitted" from "rejected: mailbox
    /// does not exist", both arrive as a bare <c>550</c>, and only one of them means we are
    /// misconfigured.
    /// </remarks>
    public string? EnhancedStatusCode { get; init; }

    public SmtpReplyClass Class => SmtpReplyClassExtensions.Classify(Code);

    /// <summary>True for 2xx.</summary>
    public bool IsPositive => Class == SmtpReplyClass.Positive;

    /// <summary>True for 3xx, the server wants more input (e.g. the DATA payload).</summary>
    public bool IsIntermediate => Class == SmtpReplyClass.Intermediate;

    /// <summary>True for 4xx, retryable.</summary>
    public bool IsTransientNegative => Class == SmtpReplyClass.TransientNegative;

    /// <summary>True for 5xx, not retryable.</summary>
    public bool IsPermanentNegative => Class == SmtpReplyClass.PermanentNegative;

    /// <summary>The reply text joined for diagnostics. Never parsed; use the fields instead.</summary>
    public string Text => string.Join(' ', Lines);

    /// <summary>
    /// The reply as it would appear on the wire, for transcripts and tests. No credentials are ever
    /// echoed into a transcript, see <see cref="SmtpTranscript"/>.
    /// </summary>
    public override string ToString()
    {
        var text = string.Join(' ', Lines);
        return text.Length == 0
            ? Code.ToString(CultureInfo.InvariantCulture)
            : $"{Code.ToString(CultureInfo.InvariantCulture)} {text}";
    }
}

/// <summary>The four RFC 5321 reply classes.</summary>
public enum SmtpReplyClass
{
    /// <summary>2xx, the command succeeded.</summary>
    Positive = 2,

    /// <summary>3xx, the server requires further input.</summary>
    Intermediate = 3,

    /// <summary>4xx, a transient failure. The correct response is to retry later.</summary>
    TransientNegative = 4,

    /// <summary>5xx, a permanent failure. Retrying cannot help.</summary>
    PermanentNegative = 5,
}

/// <summary>Mapping from a reply code to its class.</summary>
public static class SmtpReplyClassExtensions
{
    /// <summary>
    /// Classifies a reply code by its first digit.
    /// </summary>
    /// <remarks>
    /// A code outside 2xx–5xx throws rather than being coerced into a class. An unrecognised reply
    /// is a protocol fault, and guessing that it was "probably fine" is how a 1xx or a malformed
    /// <c>000</c> gets mistaken for success.
    /// </remarks>
    public static SmtpReplyClass Classify(int code) => code switch
    {
        >= 200 and <= 299 => SmtpReplyClass.Positive,
        >= 300 and <= 399 => SmtpReplyClass.Intermediate,
        >= 400 and <= 499 => SmtpReplyClass.TransientNegative,
        >= 500 and <= 599 => SmtpReplyClass.PermanentNegative,
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "An SMTP reply code must be in 200-599. Any other value is a protocol fault, not a class."),
    };
}
