using System.Security.Cryptography;

namespace StyloMail.Mime;

/// <summary>
/// A write-only sink that hashes what it is given and stops accepting bytes at a hard limit.
/// </summary>
/// <remarks>
/// Used to digest an attached <c>message/rfc822</c> payload without materialising it. Serialising
/// an embedded message into a <see cref="MemoryStream"/> would mean an attacker chooses how much
/// memory we allocate, which is the failure this whole file exists to avoid. Past the limit the
/// stream accepts the write and records that it truncated, so the caller can report a partial
/// digest as partial rather than as a digest of the whole thing.
/// </remarks>
internal sealed class BoundedWriteStream(long limit) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long Written { get; private set; }

    public bool Truncated { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var remaining = limit - Written;
        if (remaining <= 0)
        {
            Truncated |= buffer.Length > 0;
            return;
        }

        var take = (int)Math.Min(remaining, buffer.Length);
        _hash.AppendData(buffer[..take]);
        Written += take;
        Truncated |= take < buffer.Length;
    }

    public override void Flush()
    {
        // Nothing is buffered: every byte is hashed as it arrives.
    }

    public byte[] Digest() => _hash.GetHashAndReset();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
