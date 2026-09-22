using System.Buffers;
using System.Text;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// Reads bounded, CRLF-terminated lines from a stream, with an injected clock.
/// </summary>
/// <remarks>
/// This reader exists only for the authentication dialogue. Once a session is authenticated it
/// becomes a byte relay and this type is never touched again, which is why its line bound can be
/// small (see <see cref="AccessProxyBounds.MaxCommandLineBytes"/>) and why a giant FETCH literal is
/// not a problem it has to solve.
///
/// <para>
/// The distinction matters for correctness, not just for tuning. A reader that could be pointed at
/// the whole session would have to understand IMAP literals and resp-text to know where a line
/// ends, and every one of those decisions is a place a message could be re-framed on its way past.
/// Keeping this reader in front of the relay, and the relay ignorant of lines, is what keeps
/// "we did not rewrite the message" true by construction.
/// </para>
/// </remarks>
internal sealed class BoundedLineReader
{
    private readonly Stream _input;
    private readonly AccessProxyBounds _bounds;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _chunk = new byte[1024];
    private int _chunkOffset;
    private int _chunkCount;

    internal BoundedLineReader(Stream input, AccessProxyBounds bounds, TimeProvider timeProvider)
    {
        _input = input;
        _bounds = bounds;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Reads one line, returning it without its terminator, or null at a clean end of stream.
    /// </summary>
    /// <exception cref="AccessProxyProtocolException">The line exceeded the configured bound.</exception>
    /// <exception cref="AccessProxyTimeoutException">No line arrived within <paramref name="timeout"/>.</exception>
    internal async ValueTask<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var accumulator = new ArrayBufferWriter<byte>(initialCapacity: 128);

        while (true)
        {
            if (_chunkOffset >= _chunkCount && !await FillAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                // End of stream. A partial line is a truncated command, which is a protocol
                // violation rather than an ordinary disconnect: the peer stopped mid-sentence.
                return accumulator.WrittenCount == 0
                    ? null
                    : throw new AccessProxyProtocolException(
                        "Peer closed the connection part-way through a command line.");
            }

            var slice = _chunk.AsSpan(_chunkOffset, _chunkCount - _chunkOffset);
            var newline = slice.IndexOf((byte)'\n');

            if (newline < 0)
            {
                AppendBounded(accumulator, slice);
                _chunkOffset = _chunkCount;
                continue;
            }

            AppendBounded(accumulator, slice[..newline]);
            _chunkOffset += newline + 1;

            var line = accumulator.WrittenSpan;
            // Strip a trailing CR. Bare LF is tolerated on read because rejecting it would break
            // clients that are otherwise fine, and it costs nothing to accept.
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            return Encoding.UTF8.GetString(line);
        }
    }

    private void AppendBounded(ArrayBufferWriter<byte> accumulator, ReadOnlySpan<byte> bytes)
    {
        if (accumulator.WrittenCount + bytes.Length > _bounds.MaxCommandLineBytes)
        {
            throw new AccessProxyProtocolException(
                $"Command line exceeded the {_bounds.MaxCommandLineBytes}-byte bound.");
        }

        accumulator.Write(bytes);
    }

    private async ValueTask<bool> FillAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            _chunkCount = await _input
                .ReadAsync(_chunk, cancellationToken)
                .AsTask()
                .WaitAsync(timeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new AccessProxyTimeoutException(
                $"No client data within {timeout}.", ex);
        }

        _chunkOffset = 0;
        return _chunkCount > 0;
    }
}

/// <summary>Writes protocol lines, with CRLF framing and a bound on what we emit.</summary>
internal sealed class ProtocolLineWriter
{
    private readonly Stream _output;

    internal ProtocolLineWriter(Stream output) => _output = output;

    /// <summary>
    /// Writes a line terminated with CRLF.
    /// </summary>
    /// <remarks>
    /// The text is developer-supplied protocol text or, in one case, a string echoed back from the
    /// client. That echo is bounded by the caller and never carries a credential, see the IMAP
    /// authenticator, which validates the tag before using it.
    /// </remarks>
    internal async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a line whose content is already bytes, appending CRLF.
    /// </summary>
    /// <remarks>
    /// This exists so a credential can be put on the wire without first becoming a
    /// <see cref="string"/>. A <c>string</c> cannot be zeroed, so building <c>$"PASS {password}"</c>
    /// would leave a copy of the password in managed memory for the life of the session, which is
    /// exactly the lifetime a mail proxy cannot bound. Taking a span keeps the secret in the buffer
    /// the caller already controls and can clear.
    /// </remarks>
    internal async ValueTask WriteRawLineAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var frame = new byte[content.Length + 2];
        content.Span.CopyTo(frame);
        frame[^2] = (byte)'\r';
        frame[^1] = (byte)'\n';

        try
        {
            await _output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The frame held a copy of the credential; clear it rather than leaving it for the GC.
            Array.Clear(frame);
        }
    }
}

/// <summary>
/// Pumps bytes between two channels without ever interpreting them.
/// </summary>
/// <remarks>
/// <b>This is the component that makes "do not rewrite message content" a structural fact rather
/// than a promise.</b> It has no parser, no line reader and no message model: it reads a fixed-size
/// buffer, writes it verbatim to the other side, and repeats. There is no branch in which a byte
/// could be altered, because there is no code that could decide to alter one.
///
/// <para>
/// Memory is constant per session and independent of message size, a client fetching a 100 MB
/// mailbox and one fetching a 1 KB note both use <see cref="AccessProxyBounds.RelayBufferBytes"/>
/// in each direction. That is what makes the concurrency bound in
/// <see cref="AccessProxyBounds.MaxConcurrentSessions"/> a meaningful promise rather than a hope.
/// </para>
///
/// <para>
/// <b>Either direction ending ends the session.</b> A client that disconnects closes the backend
/// connection with it, and a backend that closes is surfaced to the client as the end of the
/// stream rather than as a hang, the failure mode spec §9.1 assigns to this subsystem is "the
/// client sees the session drop", and a drop is better than a stall.
/// </para>
/// </remarks>
internal static class ByteRelay
{
    /// <returns>
    /// True when the session ended because a bound fired rather than because a peer closed it.
    /// </returns>
    /// <remarks>
    /// The distinction is reported rather than swallowed because the two mean opposite things to an
    /// operator: a peer closing is a client that finished, while a bound firing is a session that
    /// had to be reclaimed, a client left open, or a provider that stopped answering. Collapsing
    /// them into "relayed" would hide the second inside the first.
    /// </remarks>
    internal static async Task<bool> RunAsync(
        IDuplexChannel client,
        IDuplexChannel backend,
        AccessProxyBounds bounds,
        TimeProvider timeProvider,
        IRetrievalObserver? observer,
        CancellationToken cancellationToken)
    {
        using var session = new TimeoutScope(bounds.MaxSessionDuration, timeProvider, cancellationToken);

        var upstream = PumpAsync(
            client.Input, backend.Output, bounds, timeProvider, observer: null, session.Token);
        var downstream = PumpAsync(
            backend.Input, client.Output, bounds, timeProvider, observer, session.Token);

        // Whichever direction finishes first ends the session; the other is cancelled rather than
        // awaited, because a peer that has gone away will never close its own half.
        await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        session.Cancel();

        var upstreamBounded = await Observe(upstream).ConfigureAwait(false);
        var downstreamBounded = await Observe(downstream).ConfigureAwait(false);

        // A session cut short by the overall duration bound is also a bound firing, and it shows up
        // as cancellation rather than as a timeout exception. Note this asks whether the *deadline*
        // elapsed, not whether the linked token is cancelled, the latter is also true when we
        // cancelled deliberately because a peer closed, which is not a bound firing at all.
        return upstreamBounded || downstreamBounded || session.DeadlineElapsed;
    }

    private static async Task PumpAsync(
        Stream from,
        Stream to,
        AccessProxyBounds bounds,
        TimeProvider timeProvider,
        IRetrievalObserver? observer,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bounds.RelayBufferBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await from
                        .ReadAsync(buffer.AsMemory(0, bounds.RelayBufferBytes), cancellationToken)
                        .AsTask()
                        .WaitAsync(bounds.IdleTimeout, timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new AccessProxyTimeoutException(
                        $"Session idle for {bounds.IdleTimeout}.", ex);
                }

                if (read == 0)
                {
                    return;
                }

                // The observer sees the bytes on their way past and is deliberately given a span
                // rather than an array: it cannot retain the buffer, so it must copy anything it
                // wants to keep. Called synchronously, see IRetrievalObserver for why.
                Observe(bytes: buffer.AsSpan(0, read), observer: observer);

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The other direction ended the session, or the duration bound fired. Not an error.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Offers a chunk to the observer, isolating the session from anything it does.
    /// </summary>
    /// <remarks>
    /// <b>An assessment failure must never become a mail failure.</b> The contract on
    /// <see cref="IRetrievalObserver"/> says implementations must not throw, but a contract that is
    /// only documented is a contract that will eventually be broken, and the consequence here would
    /// be a user's mailbox dying because a semantic classifier had a bad day. That is precisely the
    /// coupling this whole seam exists to prevent, so it is enforced rather than trusted.
    ///
    /// <para>
    /// Swallowing is the deliberate choice, and it has a cost worth naming: a broken observer
    /// degrades to no evidence with no signal, which is the "reports success without doing the
    /// thing" shape this fleet has been hunting all session. The mitigation is that the observer is
    /// responsible for its own error reporting, it is the component that knows what went wrong,
    /// and it can report it where it actually believes it belongs. Surfacing it here would mean a
    /// metric dimension on the relay path, which is a decision for whoever wires the first real
    /// observer rather than something to invent now.
    /// </para>
    /// </remarks>
    private static void Observe(ReadOnlySpan<byte> bytes, IRetrievalObserver? observer)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.Observe(bytes);
        }
        catch (Exception)
        {
            // Isolation, not approval. See the remarks.
        }
    }

    /// <returns>True when the pump ended because the idle bound fired.</returns>
    private static async Task<bool> Observe(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (AccessProxyTimeoutException)
        {
            // An idle session ending is the bound doing its job, not a failure to report upward,             // but it is reported as a bound firing rather than as a clean close.
            return true;
        }
        catch (IOException)
        {
            // A peer that vanished mid-write. The session is over either way.
            return false;
        }
    }
}
