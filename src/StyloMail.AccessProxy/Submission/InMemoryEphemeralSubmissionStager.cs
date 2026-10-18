namespace StyloMail.AccessProxy.Submission;

public readonly record struct SubmissionStagingCounters(int ActiveStagers, long CapacityBytes);

/// <summary>Atomic no-wait reservations for fixed backing arrays, before SMTP 354.</summary>
internal sealed class SubmissionStagingBudget
{
    private long _state;
    private const long OneHandle = 1L << 32;
    internal SubmissionStagingCounters Counters
    {
        get { var state = Interlocked.Read(ref _state); return new((int)(state >> 32), state & uint.MaxValue); }
    }
    internal bool Reserve(int capacity)
    {
        while (true)
        {
            var state = Interlocked.Read(ref _state);
            if ((state >> 32) >= 8 || capacity > 256L * 1024 * 1024 - (state & uint.MaxValue)) return false;
            if (Interlocked.CompareExchange(ref _state, state + OneHandle + capacity, state) == state) return true;
        }
    }
    internal void Release(int capacity) => Interlocked.Add(ref _state, -OneHandle - capacity);
}

public sealed class ProxySubmissionCapacityException() : IOException("Submission staging capacity is unavailable.");
public sealed class ProxySubmissionSizeLimitException() : IOException("Submission exceeds its fixed staging bound.");

/// <summary>Fixed-capacity ephemeral staging, eight shared handles at most, no waiter queue.</summary>
public sealed class InMemoryEphemeralSubmissionStager : IEphemeralSubmissionStager
{
    internal const int MaximumMessageBytes = 25 * 1024 * 1024;
    private static readonly SubmissionStagingBudget Shared = new();
    private readonly SubmissionStagingBudget _budget;
    private int _active;
    public static SubmissionStagingCounters Counters => Shared.Counters;
    public int ActiveCount => Volatile.Read(ref _active);
    public InMemoryEphemeralSubmissionStager() : this(Shared) { }
    internal InMemoryEphemeralSubmissionStager(SubmissionStagingBudget budget) => _budget = budget;

    public ValueTask<IStagedProxySubmission> CreateAsync(long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > MaximumMessageBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var capacity = (int)maximumBytes;
        if (!_budget.Reserve(capacity)) throw new ProxySubmissionCapacityException();
        byte[] buffer = [];
        try
        {
            buffer = new byte[capacity];
            var handle = new Handle(this, buffer);
            Interlocked.Increment(ref _active);
            return ValueTask.FromResult<IStagedProxySubmission>(handle);
        }
        catch
        {
            Array.Clear(buffer);
            _budget.Release(capacity);
            throw;
        }
    }

    public ValueTask DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private sealed class Handle(InMemoryEphemeralSubmissionStager owner, byte[] buffer) : IStagedProxySubmission
    {
        private readonly BoundedContent _content = new(buffer);
        private int _disposed;
        public Stream Content => _content;
        public long Length => _content.Length;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _content.Dispose();
                Array.Clear(buffer);
                owner._budget.Release(buffer.Length);
                Interlocked.Decrement(ref owner._active);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BoundedContent : Stream
    {
        private readonly MemoryStream _stream;
        private readonly int _capacity;
        public BoundedContent(byte[] buffer)
        {
            _capacity = buffer.Length;
            _stream = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: false);
            _stream.SetLength(0);
        }
        private void CheckWrite(int count)
        {
            if (count > _capacity - Position) throw new ProxySubmissionSizeLimitException();
        }
        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position
        {
            get => _stream.Position;
            set { if (value < 0 || value > _capacity) throw new ProxySubmissionSizeLimitException(); _stream.Position = value; }
        }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); _stream.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value) { CheckWrite(1); _stream.WriteByte(value); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void SetLength(long value)
        { if (value < 0 || value > _capacity) throw new ProxySubmissionSizeLimitException(); _stream.SetLength(value); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }));
            return Position;
        }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _stream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _stream.ReadAsync(buffer, cancellationToken);
        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
