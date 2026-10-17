using System.Text;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>Hard limits for the optional, off-relay IMAP FETCH-literal analysis copy.</summary>
public sealed record ProxyRetrievalWarningAssemblerOptions
{
    public int MaximumMessageBytes { get; init; } = 256 * 1024;
    public int MaximumSessionBufferedBytes { get; init; } = 1024 * 1024;
}

/// <summary>Creates account-bound, background FETCH-literal assemblers when retrieval analysis is enabled.</summary>
public sealed class ProxyRetrievalWarningAssemblerFactory : IProxyRetrievalWarningAssemblerFactory
{
    private readonly IProxyWarningEventSink _sink;
    private readonly IProxyWarningAnalyzer _analyzer;
    private readonly TimeProvider _timeProvider;
    private readonly ProxyRetrievalWarningAssemblerOptions _options;

    public ProxyRetrievalWarningAssemblerFactory(
        IProxyWarningEventSink sink,
        IProxyWarningAnalyzer analyzer,
        TimeProvider timeProvider,
        ProxyRetrievalWarningAssemblerOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? new ProxyRetrievalWarningAssemblerOptions();
        if (_options.MaximumMessageBytes < 1 || _options.MaximumSessionBufferedBytes < _options.MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Session cap must be at least the per-message cap.");
    }

    public IProxyRetrievalWarningAssembler Create(string tenantId, string accountId) =>
        new ProxyRetrievalWarningAssembler(tenantId, accountId, _sink, _analyzer, _timeProvider, _options);
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
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<byte> _line = [];
    private readonly Task _worker;
    private byte[]? _literal;
    private int _literalWritten;
    private long _literalRemaining;
    private bool _literalUnreadable;
    private int _reservedBytes;
    private int _disposed;

    public ProxyRetrievalWarningAssembler(
        string tenantId,
        string accountId,
        IProxyWarningEventSink sink,
        IProxyWarningAnalyzer analyzer,
        TimeProvider timeProvider,
        ProxyRetrievalWarningAssemblerOptions options)
    {
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("Tenant is required.", nameof(tenantId)) : tenantId;
        _accountId = string.IsNullOrWhiteSpace(accountId) ? throw new ArgumentException("Account is required.", nameof(accountId)) : accountId;
        _sink = sink;
        _analyzer = analyzer;
        _timeProvider = timeProvider;
        _options = options;
        _worker = Task.Run(ProcessAsync);
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || Volatile.Read(ref _disposed) != 0 || !TryReserve(bytes.Length))
            return;

        var copy = GC.AllocateUninitializedArray<byte>(bytes.Length);
        bytes.CopyTo(copy);
        if (!_chunks.Writer.TryWrite(copy))
        {
            Array.Clear(copy);
            Release(copy.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _chunks.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); }
        finally
        {
            ClearLiteral();
            ClearLine();
            _stopping.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try { await ProcessChunkAsync(chunk, _stopping.Token).ConfigureAwait(false); }
                finally { Array.Clear(chunk); Release(chunk.Length); }
            }
        }
        finally
        {
            while (_chunks.Reader.TryRead(out var chunk))
            {
                Array.Clear(chunk);
                Release(chunk.Length);
            }
        }
    }

    private async ValueTask ProcessChunkAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            if (_literalRemaining > 0)
            {
                if (!_literalUnreadable)
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
                StartLiteralIfFetchLine();
        }
    }

    private void StartLiteralIfFetchLine()
    {
        var line = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_line));
        ClearLine();
        if (!line.Contains("FETCH", StringComparison.OrdinalIgnoreCase))
            return;

        var close = line.LastIndexOf('}');
        var open = close > 0 ? line.LastIndexOf('{', close) : -1;
        if (open < 0 || close != line.Length - 3 || !long.TryParse(line.AsSpan(open + 1, close - open - 1), out var length) || length < 0)
            return;

        _literalRemaining = length;
        if (length > _options.MaximumMessageBytes || length > int.MaxValue || !TryReserve((int)length))
        {
            _literalUnreadable = true;
            return;
        }

        _literal = GC.AllocateUninitializedArray<byte>((int)length);
        _literalWritten = 0;
        _literalUnreadable = false;
        if (length == 0)
            _ = CompleteLiteralAsync(CancellationToken.None);
    }

    private async ValueTask CompleteLiteralAsync(CancellationToken cancellationToken)
    {
        var literal = _literal;
        var unreadable = _literalUnreadable;
        _literal = null;
        _literalWritten = 0;
        _literalUnreadable = false;

        try
        {
            var message = unreadable ? ReadOnlyMemory<byte>.Empty : literal!.AsMemory();
            var input = new ProxyWarningAnalysisInput
            {
                Message = message,
                TenantId = _tenantId,
                AccountId = _accountId,
                ObservedAt = _timeProvider.GetUtcNow(),
            };
            var analysis = await _analyzer.AnalyzeAsync(input, cancellationToken).ConfigureAwait(false);
            var availability = unreadable ? ProxyWarningAvailability.NotProduced : analysis.Availability;
            var reason = unreadable ? ProxyWarningAvailabilityReason.UnreadableMessage : analysis.AvailabilityReason;
            await _sink.RecordOutcomeAsync(new ProxyWarningAnalysisOutcome
            {
                TenantId = _tenantId, AccountId = _accountId, TrafficKind = MessageTrafficKind.Retrieval,
                MessageToken = analysis.MessageToken, ThreadToken = analysis.ThreadToken, ObservedAt = input.ObservedAt,
                Availability = availability, AvailabilityReason = reason,
                MarkerCount = availability == ProxyWarningAvailability.Available ? analysis.Findings.Count : 0,
            }, cancellationToken).ConfigureAwait(false);
            if (availability != ProxyWarningAvailability.Available)
                return;

            foreach (var finding in analysis.Findings)
                await _sink.RecordMarkerAsync(new ProxyDeterministicWarningEvent
                {
                    TenantId = _tenantId, AccountId = _accountId, TrafficKind = MessageTrafficKind.Retrieval,
                    MessageToken = analysis.MessageToken, ThreadToken = analysis.ThreadToken,
                    MarkerKind = finding.MarkerKind, FindingId = finding.FindingId, ObservedAt = input.ObservedAt,
                    Availability = ProxyWarningAvailability.Available, AvailabilityReason = ProxyWarningAvailabilityReason.None,
                }, cancellationToken).ConfigureAwait(false);
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

    private bool TryReserve(int bytes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _reservedBytes);
            if (bytes > _options.MaximumSessionBufferedBytes - current)
                return false;
            if (Interlocked.CompareExchange(ref _reservedBytes, current + bytes, current) == current)
                return true;
        }
    }

    private void Release(int bytes) => Interlocked.Add(ref _reservedBytes, -bytes);

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
