using System.Text;

namespace StyloMail.Mime.Tests;

/// <summary>
/// Builds messages in memory for the cases a file fixture cannot conveniently express, /// nesting bombs, header floods, hidden text.
/// </summary>
internal static class SyntheticMessage
{
    public const string BaselineHeaders = """
        From: Alice Example <alice@example.com>
        To: Bob Example <bob@example.org>
        Subject: Synthetic
        Date: Mon, 22 Sep 2026 09:15:00 +0100
        Message-ID: <synthetic@example.com>
        MIME-Version: 1.0
        """;

    /// <summary>Wraps a body in the baseline headers and a content type.</summary>
    public static byte[] WithBody(string body, string contentType = "text/plain; charset=utf-8") =>
        Utf8($"{BaselineHeaders}\r\nContent-Type: {contentType}\r\n\r\n{body}");

    /// <summary>A multipart/alternative message carrying both a plain and an HTML representation.</summary>
    public static byte[] Alternative(string plain, string html) => Utf8(
        $"""
        {BaselineHeaders}
        Content-Type: multipart/alternative; boundary="alt"

        --alt
        Content-Type: text/plain; charset=utf-8

        {plain}

        --alt
        Content-Type: text/html; charset=utf-8

        {html}

        --alt--
        """);

    /// <summary>
    /// A multipart/mixed message with a single text/plain part, wrapped in as many nested
    /// multiparts as requested. Used to prove nesting depth is bounded before parsing.
    /// </summary>
    public static byte[] NestedMultipart(int levels)
    {
        var builder = new StringBuilder();
        builder.Append(BaselineHeaders).Append("\r\n");
        builder.Append("Content-Type: multipart/mixed; boundary=\"L0\"\r\n\r\n");

        for (var level = 0; level < levels; level++)
        {
            builder.Append("--L").Append(level).Append("\r\n");
            builder.Append("Content-Type: multipart/mixed; boundary=\"L").Append(level + 1).Append("\"\r\n\r\n");
        }

        builder.Append("--L").Append(levels).Append("\r\n");
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
        builder.Append("the innermost part\r\n\r\n");

        for (var level = levels; level >= 0; level--)
        {
            builder.Append("--L").Append(level).Append("--\r\n");
        }

        return Utf8(builder.ToString());
    }

    /// <summary>A well-formed message with <paramref name="count"/> extra headers on the front.</summary>
    public static byte[] WithManyHeaders(int count)
    {
        var builder = new StringBuilder();
        builder.Append(BaselineHeaders).Append("\r\n");
        for (var i = 0; i < count; i++)
        {
            builder.Append("X-Filler-").Append(i).Append(": value\r\n");
        }

        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\nbody\r\n");
        return Utf8(builder.ToString());
    }

    /// <summary>A well-formed message with one header line of the requested length.</summary>
    public static byte[] WithLongHeaderLine(int length)
    {
        var builder = new StringBuilder();
        builder.Append(BaselineHeaders).Append("\r\n");
        builder.Append("X-Long: ").Append('a', Math.Max(0, length - 8)).Append("\r\n");
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\nbody\r\n");
        return Utf8(builder.ToString());
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
}

/// <summary>A clock that does not move, so replay produces byte-identical evidence.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
