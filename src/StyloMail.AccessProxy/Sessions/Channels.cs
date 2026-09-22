namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// A bidirectional byte channel: the client's socket, or the backend's.
/// </summary>
/// <remarks>
/// Deliberately a pair of <see cref="Stream"/>s and nothing else. There is no message model here,
/// no envelope, no parsed command — because a type that could represent a message is a type that
/// invites someone to "just fix up" a header on the way past, and rewriting signed content
/// invalidates DKIM. The proxy moves bytes; this interface is the whole reason it can claim that.
///
/// <para>
/// Both directions are separate streams rather than one duplex stream because the read and write
/// halves have genuinely different lifetimes here: the relay shuts down one direction while the
/// other drains, and a single stream type would make that a special case at every use.
/// </para>
/// </remarks>
public interface IDuplexChannel : IAsyncDisposable
{
    /// <summary>Bytes arriving from the peer.</summary>
    Stream Input { get; }

    /// <summary>Bytes departing for the peer.</summary>
    Stream Output { get; }

    /// <summary>
    /// A description safe for diagnostics, e.g. <c>client:127.0.0.1:51234</c> or <c>backend:gmail/imap</c>.
    /// </summary>
    /// <remarks>
    /// Never message content and never a credential — this string is exactly the sort of thing that
    /// ends up in a metric dimension or a log line, so what it may contain is part of its contract.
    /// </remarks>
    string Description { get; }
}

/// <summary>A duplex channel over two streams.</summary>
public sealed class StreamDuplexChannel : IDuplexChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;

    public StreamDuplexChannel(Stream input, Stream output, string description, bool ownsStreams = true)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        ArgumentException.ThrowIfNullOrEmpty(description);
        Description = description;
        _ownsStreams = ownsStreams;
    }

    public Stream Input => _input;

    public Stream Output => _output;

    public string Description { get; }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsStreams)
        {
            return;
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
    }
}
