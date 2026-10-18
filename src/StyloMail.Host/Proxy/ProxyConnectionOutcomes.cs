using Microsoft.Extensions.Hosting;
using StyloMail.AccessProxy.Hosting;
using StyloMail.AccessProxy.Sessions;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Proxy;

public sealed record ProxyConnectionBucket(string Protocol, string Outcome, string Reason,
    DateTimeOffset BucketStart, long Count);

public interface IProxyConnectionOutcomeReader
{
    IReadOnlyList<ProxyConnectionBucket> Read(string tenantId, DateTimeOffset from, DateTimeOffset through);
}

/// <summary>
/// Loss-accounted connection counters. Accept/close only tries a short memory critical section;
/// SQLite is confined to the background flush. Crash loss is at most the pending/in-flight counts.
/// </summary>
public sealed class ProxyConnectionOutcomes : BackgroundService, IProxyConnectionOutcomeSink, IProxyConnectionOutcomeReader
{
    public const int KeyLimit = 1024;
    public const int RetainedBuckets = 2016;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flush = new(1);
    private readonly HostDatabase _database;
    private readonly TimeProvider _clock;
    private readonly int _keyLimit;
    private readonly ILogger<ProxyConnectionOutcomes> _logger;
    private Dictionary<Key, long> _pending = [];
    private Dictionary<Key, long>? _retry;
    private long _dropped;
    private long _retryDropped;
    private int _disposed;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset _retrySince;
    private DateTimeOffset _retryThrough;
    internal sealed record FlushResult(long DroppedCount, DateTimeOffset IntervalStart, DateTimeOffset IntervalEnd);
    private readonly record struct Key(string Tenant, ProxyListenerProtocol Protocol, ProxyConnectionReason Reason, long Bucket);

    public ProxyConnectionOutcomes(HostDatabase database, TimeProvider clock, ILogger<ProxyConnectionOutcomes> logger)
        : this(database, clock, logger, KeyLimit) { }

    internal ProxyConnectionOutcomes(HostDatabase database, TimeProvider clock, ILogger<ProxyConnectionOutcomes> logger, int keyLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keyLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLimit, KeyLimit);
        _database = database; _clock = clock; _logger = logger; _keyLimit = keyLimit;
        _pendingSince = clock.GetUtcNow();
    }

    public static long Bucket(DateTimeOffset time) => time.ToUnixTimeSeconds() / 300 * 300;
    public static bool ValidRange(DateTimeOffset from, DateTimeOffset through, DateTimeOffset now)
        => from == DateTimeOffset.FromUnixTimeSeconds(Bucket(from))
           && through == DateTimeOffset.FromUnixTimeSeconds(Bucket(through))
           && from <= through && Bucket(from) >= Bucket(now) - (RetainedBuckets - 1L) * 300
           && Bucket(through) <= Bucket(now);

    public void Count(string tenantId, ProxyListenerProtocol protocol, ProxyConnectionReason reason)
    {
        if (string.IsNullOrEmpty(tenantId) || tenantId.Length > 256 || !Enum.IsDefined(protocol) || !Enum.IsDefined(reason))
        { AddDropped(); return; }
        if (!Monitor.TryEnter(_gate)) { AddDropped(); return; }
        try
        {
            var key = new Key(tenantId, protocol, reason, Bucket(_clock.GetUtcNow()));
            if (_pending.TryGetValue(key, out var count))
            {
                if (count == long.MaxValue) AddDropped(); else _pending[key] = count + 1;
            }
            else if (_pending.Count < _keyLimit) _pending.Add(key, 1);
            else AddDropped();
        }
        finally { Monitor.Exit(_gate); }
    }

    private void AddDropped()
    {
        long previous;
        do { previous = Volatile.Read(ref _dropped); if (previous == long.MaxValue) return; }
        while (Interlocked.CompareExchange(ref _dropped, previous + 1, previous) != previous);
    }

    internal async Task<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_retry is null)
            {
                lock (_gate)
                {
                    _retry = _pending; _pending = [];
                    _retrySince = _pendingSince; _retryThrough = _clock.GetUtcNow(); _pendingSince = _retryThrough;
                }
                _retryDropped = Interlocked.Exchange(ref _dropped, 0);
            }
            var now = Bucket(_clock.GetUtcNow());
            var earliest = now - (RetainedBuckets - 1L) * 300;
            var dropped = _retryDropped;
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var (key, count) in _retry)
            {
                if (key.Bucket < earliest || key.Bucket > now || key.Bucket % 300 != 0)
                { dropped = dropped > long.MaxValue - count ? long.MaxValue : dropped + count; continue; }
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO proxy_connection_outcome(tenant_id,protocol,outcome,reason,bucket,count) "
                    + "VALUES($tenant,$protocol,$outcome,$reason,$bucket,$count) ON CONFLICT(tenant_id,protocol,outcome,reason,bucket) "
                    + "DO UPDATE SET count=CASE WHEN count > 9223372036854775807-excluded.count THEN 9223372036854775807 ELSE count+excluded.count END;";
                command.Parameters.AddWithValue("$tenant", key.Tenant);
                command.Parameters.AddWithValue("$protocol", key.Protocol.ToString());
                command.Parameters.AddWithValue("$outcome", key.Reason == ProxyConnectionReason.TlsHandshakeTimeout ? nameof(SessionOutcome.Timeout) : nameof(SessionOutcome.AdmissionRejected));
                command.Parameters.AddWithValue("$reason", key.Reason.ToString());
                command.Parameters.AddWithValue("$bucket", key.Bucket);
                command.Parameters.AddWithValue("$count", count);
                command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO proxy_connection_dropped(singleton,count) VALUES(1,$count) "
                    + "ON CONFLICT(singleton) DO UPDATE SET count=CASE WHEN count > 9223372036854775807-excluded.count THEN 9223372036854775807 ELSE count+excluded.count END;"
                    + "DELETE FROM proxy_connection_outcome WHERE bucket < $earliest OR bucket > $now OR bucket % 300 != 0;";
                command.Parameters.AddWithValue("$count", dropped);
                command.Parameters.AddWithValue("$earliest", earliest);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _retry = null; _retryDropped = 0;
            return new FlushResult(dropped, _retrySince, _retryThrough);
        }
        finally { _flush.Release(); }
    }

    public IReadOnlyList<ProxyConnectionBucket> Read(string tenantId, DateTimeOffset from, DateTimeOffset through)
    {
        if (!ValidRange(from, through, _clock.GetUtcNow())) throw new ArgumentOutOfRangeException(nameof(from));
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protocol,outcome,reason,bucket,count FROM proxy_connection_outcome "
            + "WHERE tenant_id=$tenant AND bucket >= $from AND bucket <= $through AND bucket % 300=0 "
            + "ORDER BY bucket,protocol,reason LIMIT 12096;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$from", Bucket(from));
        command.Parameters.AddWithValue("$through", Bucket(through));
        using var reader = command.ExecuteReader();
        List<ProxyConnectionBucket> rows = [];
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), reader.GetInt64(4)));
        return rows;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var result = await FlushAsync(stoppingToken).ConfigureAwait(false);
                    if (result.DroppedCount > 0)
                        _logger.LogWarning("Proxy connection outcomes dropped {DroppedCount} observations during {IntervalStart} to {IntervalEnd}; persisted host-wide, not attributed to a tenant.",
                            result.DroppedCount, result.IntervalStart, result.IntervalEnd);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    var pendingDropped = Volatile.Read(ref _dropped);
                    _logger.LogWarning("Connection outcome flush failed for {IntervalStart} to {IntervalEnd}; {RetainedKeys} keys retained for retry and {DroppedCount} overflow observations pending persistence.",
                        _retrySince, _clock.GetUtcNow(), _retry?.Count ?? 0,
                        _retryDropped > long.MaxValue - pendingDropped ? long.MaxValue : _retryDropped + pendingDropped);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // One retry batch and one current batch are the complete bounded backlog.
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // No WaitHandle is allocated by this semaphore. Leave its managed state intact for an
        // already-running flush's finally; BackgroundService.Dispose signals cancellation only.
        base.Dispose();
    }
}
