namespace StyloMail.Image;

/// <summary>A reservation and release seam shared with the host's copy accounting.</summary>
public interface IImageScratchQuota
{
    bool TryReserve(int bytes);

    void Release(int bytes);
}

/// <summary>
/// A byte quota for image scratch, shared with the host's existing copy accounting.
/// </summary>
/// <remarks>
/// This type is the reservation seam, not a global: the host supplies its own quota instance so a
/// lease is charged against the same lifetime as every other copy. A lease is reserved before its
/// buffer is allocated and releases only when the caller disposes it, which is the consumer's
/// settle point.
/// </remarks>
public sealed class ImageScratchQuota : IImageScratchQuota
{
    private long _reservedBytes;

    public ImageScratchQuota(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    public long AvailableBytes => CapacityBytes - ReservedBytes;

    /// <summary>
    /// Reserves <paramref name="bytes"/> and returns a lease owning a cleared buffer, or null when
    /// the quota cannot cover it. Nothing is allocated on failure.
    /// </summary>
    public ImageScratchLease? TryRent(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (!TryReserve(bytes))
        {
            return null;
        }

        try
        {
            return new ImageScratchLease(this, bytes);
        }
        catch
        {
            Release(bytes);
            throw;
        }
    }

    public bool TryReserve(int bytes)
    {
        while (true)
        {
            var reserved = Interlocked.Read(ref _reservedBytes);
            if (bytes > CapacityBytes - reserved)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, reserved + bytes, reserved) == reserved)
            {
                return true;
            }
        }
    }

    public void Release(int bytes) => Interlocked.Add(ref _reservedBytes, -bytes);
}

/// <summary>
/// A reserved scratch buffer. Disposing it is the consumer's settle event: the bytes are cleared,
/// the reference is dropped, then the quota is returned.
/// </summary>
public sealed class ImageScratchLease : IDisposable
{
    private readonly IImageScratchQuota _quota;
    private byte[]? _buffer;

    internal ImageScratchLease(IImageScratchQuota quota, int bytes)
    {
        _quota = quota;
        _buffer = new byte[bytes];
    }

    public int Length => _buffer?.Length ?? 0;

    public bool IsDisposed => _buffer is null;

    /// <summary>The lease's buffer. Throws after dispose rather than handing out released memory.</summary>
    public Memory<byte> Buffer =>
        _buffer is { } buffer
            ? buffer
            : throw new ObjectDisposedException(nameof(ImageScratchLease));

    /// <summary>
    /// Reserves through an arbitrary quota and returns a lease, or null when the quota refuses.
    /// Nothing is allocated on refusal; allocation failure rolls the reservation back.
    /// </summary>
    public static ImageScratchLease? TryRent(IImageScratchQuota quota, int bytes)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (!quota.TryReserve(bytes))
        {
            return null;
        }

        try
        {
            return new ImageScratchLease(quota, bytes);
        }
        catch
        {
            quota.Release(bytes);
            throw;
        }
    }

    public void Dispose()
    {
        if (_buffer is not { } buffer)
        {
            return;
        }

        _buffer = null;
        Array.Clear(buffer);
        _quota.Release(buffer.Length);
    }
}
