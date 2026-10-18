using System.Threading.Channels;
using System.Diagnostics;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>Content-free, process-wide observation resource and loss counters.</summary>
public readonly record struct ProxyRetrievalObservationCounters(long BufferedBytes, int ActiveWorkers,
    int ReadySessions, long DroppedChunks, long DroppedBytes, long AbandonedStreams, bool Saturated);

/// <summary>Fixed workers, bounded ready-session references, and a shared copied-byte budget.</summary>
internal sealed class ProxyRetrievalWorkPool : IAsyncDisposable
{
    private static readonly Lazy<ProxyRetrievalWorkPool> Default = new(() => new(64L * 1024 * 1024, 4));
    internal static ProxyRetrievalWorkPool Shared => Default.Value;
    internal static ProxyRetrievalObservationCounters SharedCounters => Default.IsValueCreated ? Default.Value.Counters : default;
    private readonly Channel<QueuedWork> _ready;
    private readonly Task[] _workers;
    private readonly long _maximumBytes;
    private long _bytes, _droppedChunks, _droppedBytes, _abandoned;
    private int _active;
    private long _allBusyAt;
    private readonly object _activityGate = new();
    private readonly TimeSpan _queueWait;
    private readonly CancellationTokenSource _expiryStopping = new();
    private readonly Task _expiry;

    internal ProxyRetrievalWorkPool(long maximumBytes, int workers, int readySessions = 64, TimeSpan? queueWait = null)
    {
        if (maximumBytes is < 1 or > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (workers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(workers));
        if (readySessions is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(readySessions));
        _maximumBytes = maximumBytes;
        _queueWait = queueWait ?? TimeSpan.FromSeconds(5);
        if (_queueWait <= TimeSpan.Zero || _queueWait > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(queueWait));
        _ready = Channel.CreateBounded<QueuedWork>(new BoundedChannelOptions(readySessions)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkAsync)).ToArray();
        _expiry = Task.Run(ExpireAsync);
    }

    internal ProxyRetrievalObservationCounters Counters => new(Interlocked.Read(ref _bytes), Volatile.Read(ref _active),
        _ready.Reader.Count, Interlocked.Read(ref _droppedChunks), Interlocked.Read(ref _droppedBytes), Interlocked.Read(ref _abandoned), Saturated);
    private bool Saturated
    {
        get
        {
            var since = Interlocked.Read(ref _allBusyAt);
            return Volatile.Read(ref _active) == _workers.Length && since != 0 && Stopwatch.GetElapsedTime(since) >= _queueWait;
        }
    }
    internal bool Schedule(ProxyRetrievalWarningAssembler assembler)
    {
        if (Saturated) return false;
        return _ready.Writer.TryWrite(new QueuedWork(assembler));
    }
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
        await foreach (var work in _ready.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!work.TryClaim()) continue;
            if (work.Expired(_queueWait)) { work.Assembler.ExpireQueued(); continue; }
            lock (_activityGate)
            {
                if (Interlocked.Increment(ref _active) == _workers.Length)
                    Interlocked.Exchange(ref _allBusyAt, Stopwatch.GetTimestamp());
            }
            try { await work.Assembler.ProcessOneAsync().ConfigureAwait(false); }
            finally
            {
                lock (_activityGate)
                {
                    Interlocked.Decrement(ref _active);
                    Interlocked.Exchange(ref _allBusyAt, 0);
                }
                work.Assembler.WorkCompleted();
            }
        }
    }
    private async Task ExpireAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(_expiryStopping.Token).ConfigureAwait(false))
            {
                // Inspect at most one bounded queue snapshot per tick. A CAS claim gives either
                // a worker or this sweeper exclusive parser ownership before any buffer cleanup.
                var count = _ready.Reader.Count;
                for (var i = 0; i < count && _ready.Reader.TryRead(out var work); i++)
                {
                    if (!work.Expired(_queueWait) && _ready.Writer.TryWrite(work)) continue;
                    if (work.TryClaim()) work.Assembler.ExpireQueued();
                }
            }
        }
        catch (OperationCanceledException) when (_expiryStopping.IsCancellationRequested) { }
    }
    private sealed class QueuedWork(ProxyRetrievalWarningAssembler assembler)
    {
        private readonly long _queuedAt = Stopwatch.GetTimestamp();
        private int _claimed;
        public ProxyRetrievalWarningAssembler Assembler { get; } = assembler;
        public bool Expired(TimeSpan wait) => Stopwatch.GetElapsedTime(_queuedAt) >= wait;
        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }
    public async ValueTask DisposeAsync()
    {
        await _expiryStopping.CancelAsync().ConfigureAwait(false);
        await _expiry.ConfigureAwait(false);
        _expiryStopping.Dispose();
        _ready.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }
}
