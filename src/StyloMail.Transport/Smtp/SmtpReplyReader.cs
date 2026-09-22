using System.Globalization;
using System.Text;

namespace StyloMail.Transport.Smtp;

/// <summary>
/// Reads bounded, reassembled SMTP replies from a stream.
/// </summary>
/// <remarks>
/// Deliberately not a <c>StreamReader</c>. <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>
/// has no line-length limit, so a peer that never sends a line terminator makes it allocate until
/// the process dies, the exact denial-of-service the transport exists to prevent. This reader
/// fails at <see cref="SmtpBounds.MaxReplyLineBytes"/> instead, and fails again at
/// <see cref="SmtpBounds.MaxReplyLines"/> and <see cref="SmtpBounds.MaxReplyBytes"/> when a hostile
/// server tries the same trick across continuation lines.
///
/// <para>
/// One reader per connection, used by one thread at a time. The line buffer is reused across calls,
/// which is safe under that usage and avoids a per-line allocation on the hot path.
/// </para>
/// </remarks>
internal sealed class SmtpReplyReader
{
    private readonly Stream _stream;
    private readonly SmtpBounds _bounds;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly byte[] _lineBuffer;

    private int _readPosition;
    private int _readLength;

    internal SmtpReplyReader(Stream stream, SmtpBounds bounds, TimeProvider timeProvider)
    {
        _stream = stream;
        _bounds = bounds;
        _timeProvider = timeProvider;

        // +1 so a line exactly at the limit is accepted while the next byte proves the overflow.
        _lineBuffer = new byte[bounds.MaxReplyLineBytes + 1];
    }

    /// <summary>Bytes currently buffered but not yet consumed. Used by the transcript.</summary>
    internal int BufferedByteCount => _readLength - _readPosition;

    /// <summary>
    /// Reads one complete reply, following continuation lines to the terminating one.
    /// </summary>
    /// <exception cref="SmtpTimeoutException">The peer did not answer within <paramref name="timeout"/>.</exception>
    /// <exception cref="SmtpConnectionLostException">The peer closed or the stream failed.</exception>
    /// <exception cref="SmtpProtocolException">The reply was malformed or over budget.</exception>
    internal async ValueTask<SmtpReply> ReadReplyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lines = new List<string>(4);
        var totalBytes = 0;
        int? code = null;
        var enhanced = (string?)null;

        while (true)
        {
            var line = await ReadLineAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new SmtpConnectionLostException(
                    code is null
                        ? "The peer closed the connection without sending a reply."
                        : "The peer closed the connection part-way through a multi-line reply.");
            }

            totalBytes += line.Length;
            if (totalBytes > _bounds.MaxReplyBytes)
            {
                throw new SmtpProtocolException(
                    $"The reply exceeded the {_bounds.MaxReplyBytes}-byte budget before it terminated.");
            }

            if (line.Length < 3
                || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var lineCode)
                || (line.Length > 3 && line[3] != ' ' && line[3] != '-'))
            {
                throw new SmtpProtocolException(
                    $"Malformed reply line '{Truncate(line)}': expected a three-digit code followed by a space or hyphen.");
            }

            if (lineCode is < 200 or > 599)
            {
                // 1xx is a positive preliminary reply we never solicit, and 0xx/6xx+ do not exist.
                // Treating an unexpected code as success would be the worst possible reading of it.
                throw new SmtpProtocolException(
                    $"Reply code {lineCode.ToString(CultureInfo.InvariantCulture)} is outside 200-599 and cannot be classified.");
            }

            if (code is null)
            {
                code = lineCode;
            }
            else if (lineCode != code)
            {
                // Continuation lines must repeat the original code. A server changing code mid-reply
                // is telling us something we do not have a rule for, so we refuse rather than guess.
                throw new SmtpProtocolException(
                    $"Reply code changed from {code.Value.ToString(CultureInfo.InvariantCulture)} to " +
                    $"{lineCode.ToString(CultureInfo.InvariantCulture)} inside one multi-line reply.");
            }

            var text = line.Length > 4 ? line[4..] : string.Empty;
            lines.Add(text);

            if (lines.Count > _bounds.MaxReplyLines)
            {
                throw new SmtpProtocolException(
                    $"The reply exceeded the {_bounds.MaxReplyLines}-line budget before it terminated.");
            }

            if (line.Length == 3 || line[3] == ' ')
            {
                // The terminating line: no hyphen, so the reply is complete.
                enhanced ??= TryFindEnhancedStatusCode(lines);
                return new SmtpReply { Code = code.Value, Lines = lines, EnhancedStatusCode = enhanced };
            }
        }
    }

    /// <summary>
    /// Reads a single line, returning <c>null</c> at a clean end of stream.
    /// </summary>
    /// <remarks>
    /// Accepts both CRLF and a bare LF. RFC 5321 requires CRLF, but a strict reading here would
    /// reject working servers for a defect that changes nothing about what the line says, and
    /// nothing trusts the framing, because the parsed values are length-bounded either way.
    /// </remarks>
    private async ValueTask<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var length = 0;

        while (true)
        {
            if (_readPosition >= _readLength && !await FillAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                return length == 0
                    ? null
                    : throw new SmtpConnectionLostException("The connection ended part-way through a reply line.");
            }

            var b = _readBuffer[_readPosition++];

            if (b == (byte)'\n')
            {
                // Strip the CR of a CRLF pair; leave a bare CR alone, since it was not a terminator.
                if (length > 0 && _lineBuffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                return Encoding.UTF8.GetString(_lineBuffer, 0, length);
            }

            if (length >= _bounds.MaxReplyLineBytes)
            {
                throw new SmtpProtocolException(
                    $"A reply line exceeded the {_bounds.MaxReplyLineBytes}-byte limit without a terminator.");
            }

            _lineBuffer[length++] = b;
        }
    }

    /// <summary>Refills the read buffer. Returns false at end of stream.</summary>
    private async ValueTask<bool> FillAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_readPosition > 0)
        {
            _readPosition = 0;
            _readLength = 0;
        }

        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        int read;
        try
        {
            read = await _stream.ReadAsync(_readBuffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SmtpTimeoutException(
                $"The peer sent nothing for {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
        }
        catch (IOException ex)
        {
            throw new SmtpConnectionLostException("The connection failed while waiting for a reply.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new SmtpConnectionLostException("The connection was closed while waiting for a reply.", ex);
        }

        if (read == 0)
        {
            return false;
        }

        _readLength = read;
        return true;
    }

    /// <summary>
    /// Finds an RFC 3463 enhanced status code across the reply's lines.
    /// </summary>
    /// <remarks>
    /// Scanned manually rather than with a regex: this runs on every reply, and the grammar is a
    /// single token of three dot-separated integers. It is reported as advisory detail, a reply
    /// whose code is <c>550</c> is still a 5xx whether or not an enhanced code accompanies it.
    /// </remarks>
    private static string? TryFindEnhancedStatusCode(List<string> lines)
    {
        foreach (var line in lines)
        {
            var token = FirstToken(line);
            if (token.Length >= 5 && LooksLikeEnhancedStatusCode(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string FirstToken(string line)
    {
        var end = line.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? line : line[..end];
    }

    private static bool LooksLikeEnhancedStatusCode(string token)
    {
        var dots = 0;
        var digitsInSegment = 0;

        foreach (var c in token)
        {
            if (c == '.')
            {
                if (digitsInSegment == 0 || dots == 2)
                {
                    return false;
                }

                dots++;
                digitsInSegment = 0;
            }
            else if (char.IsAsciiDigit(c))
            {
                digitsInSegment++;
            }
            else
            {
                return false;
            }
        }

        return dots == 2 && digitsInSegment > 0;
    }

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : string.Concat(value.AsSpan(0, 80), "…");
}
