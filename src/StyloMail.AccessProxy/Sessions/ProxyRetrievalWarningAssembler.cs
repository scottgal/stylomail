using System.Text;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Submission;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>Hard limits for the optional, off-relay IMAP FETCH-literal analysis copy.</summary>
public sealed record ProxyRetrievalWarningAssemblerOptions
{
    public int MaximumMessageBytes { get; init; } = 256 * 1024;
    public int MaximumSessionBufferedBytes { get; init; } = 1024 * 1024;
    public int MaximumQueuedChunks { get; init; } = 64;
}

/// <summary>Creates account-bound, background FETCH-literal assemblers when retrieval analysis is enabled.</summary>
public sealed class ProxyRetrievalWarningAssemblerFactory : IProxyRetrievalWarningAssemblerFactory
{
    private readonly IProxyWarningEventSink _sink;
    private readonly IProxyWarningAnalyzer _analyzer;
    private readonly TimeProvider _timeProvider;
    private readonly ProxyRetrievalWarningAssemblerOptions _options;
    private readonly ProxyElementOrchestrator? _elementOrchestrator;
    private readonly ProxyRetrievalWorkPool _pool;
    public static ProxyRetrievalObservationCounters Counters => ProxyRetrievalWorkPool.SharedCounters;

    public ProxyRetrievalWarningAssemblerFactory(
        IProxyWarningEventSink sink,
        IProxyWarningAnalyzer analyzer,
        TimeProvider timeProvider,
        ProxyRetrievalWarningAssemblerOptions? options = null,
        ProxyElementOrchestrator? elementOrchestrator = null)
        : this(sink, analyzer, timeProvider, options, elementOrchestrator, ProxyRetrievalWorkPool.Shared) { }

    internal ProxyRetrievalWarningAssemblerFactory(IProxyWarningEventSink sink, IProxyWarningAnalyzer analyzer,
        TimeProvider timeProvider, ProxyRetrievalWarningAssemblerOptions? options,
        ProxyElementOrchestrator? elementOrchestrator, ProxyRetrievalWorkPool pool)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? new ProxyRetrievalWarningAssemblerOptions();
        _elementOrchestrator = elementOrchestrator;
        _pool = pool;
        if (_options.MaximumMessageBytes is < 1 or > 256 * 1024
            || _options.MaximumSessionBufferedBytes > 1024 * 1024
            || _options.MaximumSessionBufferedBytes < _options.MaximumMessageBytes
            || _options.MaximumQueuedChunks is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Session cap must be at least the per-message cap.");
    }

    public IProxyRetrievalWarningAssembler Create(string tenantId, string accountId) =>
        new ProxyRetrievalWarningAssembler(tenantId, accountId, _sink, _analyzer, _timeProvider, _options, _elementOrchestrator, _pool);
}

/// <summary>
/// Copies only complete IMAP FETCH literals to a bounded background worker. The relay only calls
/// <see cref="Observe"/>; it never awaits parsing, analysis, storage, or an overflow decision.
/// </summary>
internal sealed class ProxyRetrievalWarningAssembler : IProxyRetrievalWarningAssembler
{
    private const int MaximumFetchLineBytes = 4096;
    private readonly string _tenantId;
    private readonly string _accountId;
    private readonly IProxyWarningEventSink _sink;
    private readonly IProxyWarningAnalyzer _analyzer;
    private readonly TimeProvider _timeProvider;
    private readonly ProxyRetrievalWarningAssemblerOptions _options;
    private readonly ProxyElementOrchestrator? _elementOrchestrator;
    private readonly Channel<byte[]> _chunks;
    private readonly ProxyRetrievalWorkPool _pool;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<byte> _line = [];
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _scheduled, _abandoned, _cleaned;
    private readonly TaskCompletionSource _cancellationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _literal;
    private int _literalWritten;
    private long _literalRemaining;
    private bool _literalUnreadable;
    private bool _literalPartial;
    private bool _fetchResponseOpen;
    private int _reservedBytes;
    private int _disposed;

    public ProxyRetrievalWarningAssembler(
        string tenantId,
        string accountId,
        IProxyWarningEventSink sink,
        IProxyWarningAnalyzer analyzer,
        TimeProvider timeProvider,
        ProxyRetrievalWarningAssemblerOptions options,
        ProxyElementOrchestrator? elementOrchestrator, ProxyRetrievalWorkPool pool)
    {
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("Tenant is required.", nameof(tenantId)) : tenantId;
        _accountId = string.IsNullOrWhiteSpace(accountId) ? throw new ArgumentException("Account is required.", nameof(accountId)) : accountId;
        _sink = sink;
        _analyzer = analyzer;
        _timeProvider = timeProvider;
        _options = options;
        _elementOrchestrator = elementOrchestrator;
        _pool = pool;
        _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaximumQueuedChunks)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _abandoned) != 0) { _pool.Drop(1, bytes.Length); return; }
        if (!TryReserve(bytes.Length)) { Abandon(1, bytes.Length); return; }

        byte[] copy;
        try { copy = GC.AllocateUninitializedArray<byte>(bytes.Length); bytes.CopyTo(copy); }
        catch (Exception) { Release(bytes.Length); Abandon(1, bytes.Length); return; }
        if (!_chunks.Writer.TryWrite(copy))
        {
            Array.Clear(copy);
            Release(copy.Length);
            Abandon(1, copy.Length);
        }
        else Schedule();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _chunks.Writer.TryComplete();
        Schedule();
        try { await _finished.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { Abandon(0, 0); }
    }

    internal int QueuedChunks => _chunks.Reader.Count;
    internal int ReservedBytes => Volatile.Read(ref _reservedBytes);
    internal bool Abandoned => Volatile.Read(ref _abandoned) != 0;

    private void Schedule()
    {
        if (_finished.Task.IsCompleted || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;
        if (_pool.Schedule(this)) return;
        // We own the scheduling token and no worker owns this parser. A full ready queue must
        // never create another task or wait on the relay: abandon and clear bounded owned state.
        AbandonCore();
        Cleanup();
        Interlocked.Exchange(ref _scheduled, 0);
    }
    private void Abandon(int chunks, long bytes)
    {
        _pool.Drop(chunks, bytes);
        AbandonCore();
        Schedule();
    }
    private void AbandonCore()
    {
        if (Interlocked.Exchange(ref _abandoned, 1) != 0) return;
        _pool.Abandon();
        _chunks.Writer.TryComplete();
        _ = CancelAsync();
    }
    private async Task CancelAsync()
    {
        try { await _stopping.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* Optional cancellation callbacks cannot escape onto the relay. */ }
        finally { _cancellationDone.TrySetResult(); }
    }
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        while (_chunks.Reader.TryRead(out var queued))
        {
            _pool.Drop(1, queued.Length);
            Array.Clear(queued); Release(queued.Length);
        }
        ClearLiteral(); ClearLine();
        _finished.TrySetResult();
        _ = DisposeCancellationAsync();
    }
    private async Task DisposeCancellationAsync()
    {
        if (Abandoned) await _cancellationDone.Task.ConfigureAwait(false);
        _stopping.Dispose();
    }
    internal async Task ProcessOneAsync()
    {
        try
        {
            if (!Abandoned && _chunks.Reader.TryRead(out var chunk))
            {
                try
                {
                    _stopping.Token.ThrowIfCancellationRequested();
                    await ProcessChunkAsync(chunk, _stopping.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (Abandoned) _pool.Drop(1, chunk.Length);
                    Array.Clear(chunk); Release(chunk.Length);
                }
            }
        }
        catch (Exception) { AbandonCore(); }
        finally
        {
            if (Abandoned || Volatile.Read(ref _disposed) != 0 && !_chunks.Reader.TryPeek(out _)) Cleanup();
        }
    }

    internal void WorkCompleted()
    {
        // The worker returns its global permit before this parser can schedule another chunk.
        // Keep exclusive parser ownership until both processing and that release have completed.
        Interlocked.Exchange(ref _scheduled, 0);
        if (!_finished.Task.IsCompleted && (_chunks.Reader.TryPeek(out _) || Volatile.Read(ref _disposed) != 0 || Abandoned)) Schedule();
    }

    private async ValueTask ProcessChunkAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            if (Abandoned) return;
            if (_literalRemaining > 0)
            {
                if (!_literalUnreadable && !_literalPartial)
                    _literal![_literalWritten++] = chunk[index];
                _literalRemaining--;
                if (_literalRemaining == 0)
                    await CompleteLiteralAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var value = chunk[index];
            if (_line.Count < MaximumFetchLineBytes)
                _line.Add(value);
            else
                ClearLine();

            if (value == (byte)'\n')
            {
                StartLiteralIfFetchLine();
                if (_literalRemaining == 0 && (_literalPartial || _literal is not null))
                    await CompleteLiteralAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void StartLiteralIfFetchLine()
    {
        var line = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_line));
        ClearLine();
        var prefix = line.AsSpan().Trim();
        if (prefix.StartsWith("* ", StringComparison.Ordinal))
        {
            // A fresh untagged response must identify FETCH, not merely contain that substring.
            var sequenceEnd = prefix[2..].IndexOf(' ');
            if (sequenceEnd < 0 || !uint.TryParse(prefix.Slice(2, sequenceEnd), out _))
            {
                _fetchResponseOpen = false;
                return;
            }
            var response = prefix[(sequenceEnd + 3)..].TrimStart();
            _fetchResponseOpen = response.StartsWith("FETCH (", StringComparison.OrdinalIgnoreCase);
        }
        if (!_fetchResponseOpen) return;

        var close = line.LastIndexOf('}');
        var open = close > 0 ? line.LastIndexOf('{', close) : -1;
        if (open < 0 || close != line.Length - 3 || !long.TryParse(line.AsSpan(open + 1, close - open - 1), out var length) || length < 0)
        {
            _fetchResponseOpen = false;
            return;
        }

        _literalRemaining = length;
        _literalUnreadable = false;
        _literalPartial = !IsWholeMessageItem(line.AsSpan(0, open));
        if (_literalPartial)
        {
            // Consume framing but do not allocate a message buffer or send a partial to parsing.
            return;
        }
        if (length > _options.MaximumMessageBytes || length > int.MaxValue)
        {
            _literalUnreadable = true;
            return;
        }
        if (!TryReserve((int)length))
        {
            _literalUnreadable = true;
            Abandon(0, 0);
            return;
        }

        _literal = GC.AllocateUninitializedArray<byte>((int)length);
        _literalWritten = 0;
        _literalUnreadable = false;
    }

    private async ValueTask CompleteLiteralAsync(CancellationToken cancellationToken)
    {
        var literal = _literal;
        var unreadable = _literalUnreadable;
        var partial = _literalPartial;
        _literal = null;
        _literalWritten = 0;
        _literalUnreadable = false;
        _literalPartial = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partial)
            {
                // A partial FETCH does not establish message/thread identity. Use fresh opaque
                // tokens for this excluded observation; do not derive identity from its content.
                var token = "partial_" + Guid.NewGuid().ToString("N");
                var observedAt = _timeProvider.GetUtcNow();
                await _sink.RecordOutcomeAsync(new ProxyWarningAnalysisOutcome
                {
                    TenantId = _tenantId, AccountId = _accountId, TrafficKind = MessageTrafficKind.Retrieval,
                    MessageToken = token, ThreadToken = token, ObservedAt = observedAt,
                    Availability = ProxyWarningAvailability.NotProduced,
                    AvailabilityReason = ProxyWarningAvailabilityReason.UnsupportedContent, MarkerCount = 0,
                }, cancellationToken).ConfigureAwait(false);
                if (_elementOrchestrator is not null)
                    await _elementOrchestrator.RecordUnsupportedRetrievalAsync(_tenantId, _accountId,
                        token, token, observedAt, cancellationToken).ConfigureAwait(false);
                return;
            }
            var message = unreadable ? ReadOnlyMemory<byte>.Empty : literal!.AsMemory();
            var input = new ProxyWarningAnalysisInput
            {
                Message = message,
                TenantId = _tenantId,
                AccountId = _accountId,
                ObservedAt = _timeProvider.GetUtcNow(),
            };
            var analysis = await _analyzer.AnalyzeAsync(input, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var availability = unreadable ? ProxyWarningAvailability.NotProduced : analysis.Availability;
            var reason = unreadable ? ProxyWarningAvailabilityReason.UnreadableMessage : analysis.AvailabilityReason;
            await _sink.RecordOutcomeAsync(new ProxyWarningAnalysisOutcome
            {
                TenantId = _tenantId, AccountId = _accountId, TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = analysis.MessageToken, ThreadToken = analysis.ThreadToken, ObservedAt = input.ObservedAt,
                Availability = availability, AvailabilityReason = reason,
                MarkerCount = availability == ProxyWarningAvailability.Available ? analysis.Findings.Count : 0,
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (availability == ProxyWarningAvailability.Available)
            {
                foreach (var finding in analysis.Findings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _sink.RecordMarkerAsync(new ProxyDeterministicWarningEvent
                    {
                        TenantId = _tenantId, AccountId = _accountId, TrafficKind = MessageTrafficKind.Retrieval,
                        MessageToken = analysis.MessageToken, ThreadToken = analysis.ThreadToken,
                        MarkerKind = finding.MarkerKind, FindingId = finding.FindingId, ObservedAt = input.ObservedAt,
                        Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxyWarningAvailabilityReason.None,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!unreadable && _elementOrchestrator is not null)
            {
                try
                {
                    await _elementOrchestrator.EmitAsync(input, literal!, analysis.MessageToken, analysis.ThreadToken,
                        MessageTrafficKind.Retrieval, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Element classification and storage are observational; they cannot drop a fetch.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session teardown drops incomplete analysis; it never delays or fails relay.
        }
        catch
        {
            // Analysis and storage are observational and must not fail the session or worker.
        }
        finally
        {
            if (literal is not null)
            {
                Array.Clear(literal);
                Release(literal.Length);
            }
        }
    }

    private static bool IsWholeMessageItem(ReadOnlySpan<char> prefix)
    {
        prefix = prefix.TrimEnd();
        return EndsWithItem(prefix, "BODY[]") || EndsWithItem(prefix, "BODY.PEEK[]") || EndsWithItem(prefix, "RFC822");
    }

    private static bool EndsWithItem(ReadOnlySpan<char> prefix, ReadOnlySpan<char> item)
    {
        if (!prefix.EndsWith(item, StringComparison.OrdinalIgnoreCase)) return false;
        var start = prefix.Length - item.Length;
        return start == 0 || prefix[start - 1] is ' ' or '\t' or '(';
    }

    private bool TryReserve(int bytes)
    {
        if (!_pool.Reserve(bytes)) return false;
        while (true)
        {
            var current = Volatile.Read(ref _reservedBytes);
            if (bytes > _options.MaximumSessionBufferedBytes - current)
            {
                _pool.Release(bytes);
                return false;
            }
            if (Interlocked.CompareExchange(ref _reservedBytes, current + bytes, current) == current)
                return true;
        }
    }

    private void Release(int bytes)
    {
        Interlocked.Add(ref _reservedBytes, -bytes);
        _pool.Release(bytes);
    }

    private void ClearLiteral()
    {
        if (_literal is null)
            return;
        Array.Clear(_literal);
        Release(_literal.Length);
        _literal = null;
    }

    private void ClearLine()
    {
        CollectionsMarshal.AsSpan(_line).Clear();
        _line.Clear();
    }
}
