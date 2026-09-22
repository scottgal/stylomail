using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// A duplex channel that keeps everything the client sent.
/// </summary>
/// <remarks>
/// <b>This exists to produce exact bytes, not a summary.</b> When a hand-written parser rejects a
/// real client, "the proxy reported a protocol error" is not a finding anybody can act on. The
/// command that was rejected, byte for byte, is. This wraps the channel the proxy session is given
/// so the harness can print what actually arrived.
/// </remarks>
internal sealed class RecordingDuplex : IDuplexChannel
{
    private readonly IDuplexChannel _inner;
    private readonly MemoryStream _fromClient = new();
    private readonly MemoryStream _toClient = new();

    internal RecordingDuplex(IDuplexChannel inner)
    {
        _inner = inner;
        Input = new TeeReadStream(inner.Input, _fromClient);
        Output = new TeeWriteStream(inner.Output, _toClient);
    }

    public Stream Input { get; }

    public Stream Output { get; }

    /// <summary>Everything the proxy sent back, with control characters made visible.</summary>
    internal string ToClient
        => System.Text.Encoding.UTF8.GetString(_toClient.ToArray())
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n\n", StringComparison.Ordinal);

    public string Description => _inner.Description;

    /// <summary>Everything the client sent, as text, with control characters made visible.</summary>
    internal string FromClient
    {
        get
        {
            var text = System.Text.Encoding.UTF8.GetString(_fromClient.ToArray());

            // A raw CRLF would break the message this ends up inside, so they are spelled out.
            return text.Replace("\r", "\\r", StringComparison.Ordinal)
                       .Replace("\n", "\\n\n", StringComparison.Ordinal);
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>A write-only stream that copies everything it is given into a sink.</summary>
    private sealed class TeeWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _sink;

        internal TeeWriteStream(Stream inner, MemoryStream sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _sink.Write(buffer, offset, count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _sink.Write(buffer);
            _inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _sink.Write(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A read-only stream that copies everything it hands out into a sink.</summary>
    private sealed class TeeReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _sink;

        internal TeeReadStream(Stream inner, MemoryStream sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _sink.Write(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _sink.Write(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
