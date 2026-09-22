namespace StyloMail.Transport.Smtp;

/// <summary>
/// Writes a stored message into an SMTP <c>DATA</c> stream, applying exactly the two
/// transformations SMTP requires and no others.
/// </summary>
/// <remarks>
/// <b>This is the class that keeps "we do not rewrite the message" true.</b> A proxy that silently
/// modifies mail breaks the guarantees it was deployed to uphold, and the most common way that
/// happens is a client library that round-trips the message through its own object model, re-folding
/// headers and re-encoding parts. DKIM signs the canonicalised bytes, so a re-folded header is a
/// broken signature at the receiving end.
///
/// <para>
/// Only two transformations are applied, both mandated by RFC 5321:
/// </para>
/// <list type="number">
/// <item><b>Line endings become CRLF.</b> SMTP is defined over CRLF-terminated lines. A stored
/// payload with bare LF is converted. This is <em>not</em> a content rewrite: DKIM's canonicalisation
/// is defined over CRLF text, so normalising to CRLF is what preserves the signature rather than
/// what breaks it.</item>
/// <item><b>Leading dots are duplicated.</b> A line whose first character is <c>.</c> is dot-stuffed,
/// because a lone <c>.</c> on a line ends the message. The receiver removes the extra dot, so the
/// recovered message is byte-identical to the stored one.</item>
/// </list>
/// <para>
/// Headers, body text, encodings, whitespace inside lines and the order of everything are untouched.
/// Nothing here parses the message; it cannot, and that is the point.
/// </para>
/// </remarks>
internal static class SmtpDataWriter
{
    /// <summary>
    /// The end-of-data terminator, as it goes on the wire.
    /// </summary>
    /// <remarks>
    /// Just <c>.\r\n</c>, with no leading CRLF, because <see cref="WriteBodyAsync"/> has already
    /// guaranteed the body ends with one. Prefixing another would put a blank line at the end of
    /// every message, invisible in a rendered client, and a silent modification of the bytes the
    /// receiver compares against a DKIM body hash.
    /// </remarks>
    internal static ReadOnlyMemory<byte> Terminator { get; } = ".\r\n"u8.ToArray();

    /// <summary>
    /// Streams a payload as the body of a <c>DATA</c> command, without the terminating line.
    /// </summary>
    /// <param name="stream">The channel's current stream.</param>
    /// <param name="payload">The stored message bytes. Read only; never modified.</param>
    /// <param name="buffer">Scratch buffer owned by the caller, reused across calls.</param>
    /// <returns>Bytes written to the wire, including stuffing and line-ending conversion.</returns>
    internal static async ValueTask<long> WriteBodyAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Indexed through the memory rather than a hoisted span: a span is a ref struct and cannot
        // be live across the awaits below.
        var written = 0L;
        var used = 0;
        var atLineStart = true;
        var i = 0;
        var length = payload.Length;

        while (i < length)
        {
            // Each iteration appends at most two bytes, so leaving four of headroom keeps the
            // trailing CRLF below in bounds without a second length check.
            if (used > buffer.Length - 4)
            {
                await stream.WriteAsync(buffer.AsMemory(0, used), cancellationToken).ConfigureAwait(false);
                written += used;
                used = 0;
            }

            var b = payload.Span[i];

            if (b is (byte)'\r' or (byte)'\n')
            {
                // CR LF consumes both; a lone CR or a lone LF is a terminator on its own. Real MTAs
                // read it the same way, and refusing to normalise would mean rejecting mail that
                // every other server on the internet would have accepted.
                i += b == (byte)'\r' && i + 1 < length && payload.Span[i + 1] == (byte)'\n' ? 2 : 1;

                buffer[used++] = (byte)'\r';
                buffer[used++] = (byte)'\n';
                atLineStart = true;
                continue;
            }

            if (atLineStart && b == (byte)'.')
            {
                buffer[used++] = (byte)'.';
            }

            buffer[used++] = b;
            atLineStart = false;
            i++;
        }

        // A body that did not end with a line terminator still needs one, or the terminator we
        // append next would be read as part of the final line.
        if (!atLineStart)
        {
            buffer[used++] = (byte)'\r';
            buffer[used++] = (byte)'\n';
        }

        if (used > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, used), cancellationToken).ConfigureAwait(false);
            written += used;
        }

        return written;
    }

    /// <summary>
    /// Reverses <see cref="WriteBodyAsync"/>: strips stuffing and normalises line endings back.
    /// </summary>
    /// <remarks>
    /// <b>Test and diagnostic use only.</b> This is what a receiving MTA does to recover the
    /// message, and having it here is what makes "the original bytes survived the handoff" a
    /// property a test can assert rather than a claim in a comment, the fake server in the suite
    /// de-stuffs with this and the assertion compares against the stored payload.
    /// </remarks>
    internal static byte[] RecoverBody(ReadOnlySpan<byte> wire)
    {
        var output = new byte[wire.Length];
        var n = 0;
        var atLineStart = true;
        var i = 0;

        while (i < wire.Length)
        {
            var b = wire[i];

            if (b == (byte)'\r' && i + 1 < wire.Length && wire[i + 1] == (byte)'\n')
            {
                output[n++] = (byte)'\r';
                output[n++] = (byte)'\n';
                atLineStart = true;
                i += 2;
                continue;
            }

            if (atLineStart && b == (byte)'.')
            {
                // Drop the stuffing dot, keep the one that was in the message.
                i++;
                if (i < wire.Length)
                {
                    output[n++] = wire[i];
                    atLineStart = false;
                    i++;
                }

                continue;
            }

            output[n++] = b;
            atLineStart = false;
            i++;
        }

        return output.AsSpan(0, n).ToArray();
    }
}
