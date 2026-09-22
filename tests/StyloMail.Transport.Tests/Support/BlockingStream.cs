namespace StyloMail.Transport.Tests.Support;

/// <summary>
/// A stream that accepts a read and then never answers, so a timeout can be driven rather than
/// waited for.
/// </summary>
/// <remarks>
/// The transport's timeouts are the only thing standing between a silent peer and a worker pinned
/// forever, so they need to be tested. Testing one with a real delay would make the suite slower
/// and flakier for no extra information; testing one against <see cref="Time.Testing.FakeTimeProvider"/>
/// requires a peer that is still logically waiting when the clock jumps, which is what this is.
///
/// <para>
/// <see cref="EnteredRead"/> completes as soon as a read is outstanding, so a test can advance the
/// fake clock knowing the transport really is blocked in I/O and not merely slow to get there.
/// </para>
/// </remarks>
internal sealed class BlockingStream : Stream
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a read has been issued and is waiting.</summary>
    public Task EnteredRead => _entered.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _entered.TrySetResult();
        throw new NotSupportedException("This stream is asynchronous only.");
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
