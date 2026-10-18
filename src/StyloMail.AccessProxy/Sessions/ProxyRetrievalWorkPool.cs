using System.Threading.Channels;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>Content-free, process-wide observation resource and loss counters.</summary>
public readonly record struct ProxyRetrievalObservationCounters(long BufferedBytes, int ActiveWorkers,
    int ReadySessions, long DroppedChunks, long DroppedBytes, long AbandonedStreams);

/// <summary>Fixed workers, bounded ready-session references, and a shared copied-byte budget.</summary>
internal sealed class ProxyRetrievalWorkPool : IAsyncDisposable
{
    private static readonly Lazy<ProxyRetrievalWorkPool> Default = new(() => new(64L * 1024 * 1024, 4));
    internal static ProxyRetrievalWorkPool Shared => Default.Value;
    internal static ProxyRetrievalObservationCounters SharedCounters => Default.IsValueCreated ? Default.Value.Counters : default;
    private readonly Channel<ProxyRetrievalWarningAssembler> _ready;
    private readonly Task[] _workers;
    private readonly long _maximumBytes;
    private long _bytes, _droppedChunks, _droppedBytes, _abandoned;
    private int _active;

    internal ProxyRetrievalWorkPool(long maximumBytes, int workers, int readySessions = 64)
    {
        if (maximumBytes is < 1 or > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (workers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(workers));
        if (readySessions is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(readySessions));
        _maximumBytes = maximumBytes;
        _ready = Channel.CreateBounded<ProxyRetrievalWarningAssembler>(new BoundedChannelOptions(readySessions)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkAsync)).ToArray();
    }

    internal ProxyRetrievalObservationCounters Counters => new(Interlocked.Read(ref _bytes), Volatile.Read(ref _active),
        _ready.Reader.Count, Interlocked.Read(ref _droppedChunks), Interlocked.Read(ref _droppedBytes), Interlocked.Read(ref _abandoned));
    internal bool Schedule(ProxyRetrievalWarningAssembler assembler) => _ready.Writer.TryWrite(assembler);
    internal bool Reserve(int bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _bytes);
            if (bytes > _maximumBytes - current) return false;
            if (Interlocked.CompareExchange(ref _bytes, current + bytes, current) == current) return true;
        }
    }
    internal void Release(int bytes) => Interlocked.Add(ref _bytes, -bytes);
    internal void Drop(int chunks, long bytes) { Interlocked.Add(ref _droppedChunks, chunks); Interlocked.Add(ref _droppedBytes, bytes); }
    internal void Abandon() => Interlocked.Increment(ref _abandoned);
    private async Task WorkAsync()
    {
        await foreach (var assembler in _ready.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Increment(ref _active);
            try { await assembler.ProcessOneAsync().ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _ready.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }
}
