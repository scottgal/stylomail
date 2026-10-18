using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Contracts;
using StyloMail.Host.Proxy;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

public sealed class ProxyConnectionOutcomeTests
{
    private static readonly string[] ExpectedColumns = ["tenant_id", "protocol", "outcome", "reason", "bucket", "count"];
    [Fact]
    public async Task Repeated_refusals_upsert_one_identity_free_bucket_and_keep_tenants_separate()
    {
        using var host = new TestHost();
        var database = host.Services.GetRequiredService<HostDatabase>();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        using var counters = new ProxyConnectionOutcomes(database, clock, NullLogger<ProxyConnectionOutcomes>.Instance);
        for (var i = 0; i < 3; i++) counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.GlobalSessionLimitReached);
        using (var beforeFlush = database.Open())
        using (var query = beforeFlush.CreateCommand())
        {
            query.CommandText = "SELECT COUNT(*) FROM proxy_connection_outcome;";
            Assert.Equal(0L, query.ExecuteScalar()); // Count never writes inline.
        }
        await counters.FlushAsync();
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.GlobalSessionLimitReached);
        counters.Count("globex", ProxyListenerProtocol.Imap, ProxyConnectionReason.GlobalSessionLimitReached);
        await counters.FlushAsync();
        var row = Assert.Single(counters.Read("acme", clock.GetUtcNow(), clock.GetUtcNow()));
        Assert.Equal(4, row.Count);
        Assert.Equal("AdmissionRejected", row.Outcome);
        Assert.Equal("GlobalSessionLimitReached", row.Reason);
        Assert.Equal(1, Assert.Single(counters.Read("globex", clock.GetUtcNow(), clock.GetUtcNow())).Count);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(proxy_connection_outcome);";
        using var reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read()) columns.Add(reader.GetString(1));
        Assert.Equal(ExpectedColumns, columns);
    }

    [Fact]
    public async Task Bounded_key_overflow_is_persisted_and_retention_prunes_previously_visible_rows()
    {
        using var host = new TestHost();
        var database = host.Services.GetRequiredService<HostDatabase>();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        using var counters = new ProxyConnectionOutcomes(database, clock, NullLogger<ProxyConnectionOutcomes>.Instance, 1);
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count("acme", ProxyListenerProtocol.Pop3, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count("globex", ProxyListenerProtocol.Smtp, ProxyConnectionReason.GlobalSessionLimitReached);
        await counters.FlushAsync();
        var before = Assert.Single(counters.Read("acme", clock.GetUtcNow(), clock.GetUtcNow()));
        Assert.Equal("Timeout", before.Outcome);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count FROM proxy_connection_dropped WHERE singleton=1;";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        clock.Advance(TimeSpan.FromDays(7));
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.GlobalSessionLimitReached);
        await counters.FlushAsync();
        var after = Assert.Single(counters.Read("acme", clock.GetUtcNow().AddMinutes(-5), clock.GetUtcNow()));
        Assert.Equal("AdmissionRejected", after.Outcome);
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM proxy_connection_outcome;";
        Assert.Equal(1L, query.ExecuteScalar());
        Assert.Throws<ArgumentOutOfRangeException>(() => counters.Read("acme", clock.GetUtcNow().AddDays(-7), clock.GetUtcNow()));
    }

    [Fact]
    public async Task Failed_flush_retries_exact_counts_while_new_keys_and_overflow_stay_bounded()
    {
        using var host = new TestHost();
        var database = host.Services.GetRequiredService<HostDatabase>();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        using var counters = new ProxyConnectionOutcomes(database, clock, NullLogger<ProxyConnectionOutcomes>.Instance, 1);
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "DROP TABLE proxy_connection_outcome;"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => counters.FlushAsync());
        counters.Count("acme", ProxyListenerProtocol.Pop3, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count("globex", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        database.EnsureCreated();
        await counters.FlushAsync();
        await counters.FlushAsync();
        var rows = counters.Read("acme", clock.GetUtcNow(), clock.GetUtcNow());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Count));
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT count FROM proxy_connection_dropped WHERE singleton=1;";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public async Task Hosted_background_flush_persists_counts_without_a_manual_flush()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        var counters = host.Services.GetRequiredService<ProxyConnectionOutcomes>();
        counters.Count(TestPrincipals.AcmeTenant, ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ProxyConnectionOutcomesResponse? response;
        do
        {
            response = await reviewer.GetFromJsonAsync<ProxyConnectionOutcomesResponse>("/v1/proxy/connection-outcomes", deadline.Token);
            if (response!.Buckets.Count == 0) await Task.Delay(20, deadline.Token);
        } while (response.Buckets.Count == 0);
        Assert.Equal(1, Assert.Single(response.Buckets).Count);
    }

    [Fact]
    public async Task Contended_counter_returns_without_waiting_and_persists_the_drop()
    {
        using var host = new TestHost();
        var database = host.Services.GetRequiredService<HostDatabase>();
        using var clock = new BlockingClock();
        using var counters = new ProxyConnectionOutcomes(database, clock, NullLogger<ProxyConnectionOutcomes>.Instance, 1);
        clock.Block = true;
        var first = Task.Run(() => counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout));
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() => counters.Count("globex", ProxyListenerProtocol.Pop3, ProxyConnectionReason.TlsHandshakeTimeout))
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { clock.Block = false; clock.Release.Set(); await first; }
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        await counters.FlushAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(ProxyConnectionOutcomes.Bucket(clock.GetUtcNow()));
        Assert.Equal(2, Assert.Single(counters.Read("acme", now, now)).Count);
        Assert.Empty(counters.Read("globex", now, now));
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT count FROM proxy_connection_dropped WHERE singleton=1;";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public async Task Background_overflow_warning_names_persisted_count_and_interval()
    {
        using var host = new TestHost();
        var database = host.Services.GetRequiredService<HostDatabase>();
        var logger = new DropLogger();
        using var counters = new ProxyConnectionOutcomes(database, TimeProvider.System, logger, 1);
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count("acme", ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count("globex", ProxyListenerProtocol.Pop3, ProxyConnectionReason.TlsHandshakeTimeout);
        await counters.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (logger.Warnings.IsEmpty) await Task.Delay(20, deadline.Token);
            var fields = Assert.Single(logger.Warnings);
            Assert.Equal(1L, fields["DroppedCount"]);
            Assert.IsType<DateTimeOffset>(fields["IntervalStart"]);
            Assert.IsType<DateTimeOffset>(fields["IntervalEnd"]);
            using var db = database.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT count FROM proxy_connection_dropped WHERE singleton=1;";
            Assert.Equal(1L, query.ExecuteScalar());
        }
        finally { await counters.StopAsync(CancellationToken.None); }
    }

    private sealed class BlockingClock : TimeProvider, IDisposable
    {
        public volatile bool Block;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow()
        {
            if (Block) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return TimeProvider.System.GetUtcNow();
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class DropLogger : ILogger<ProxyConnectionOutcomes>
    {
        public System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object?>> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && state is IEnumerable<KeyValuePair<string, object?>> fields)
                Warnings.Enqueue(fields.ToDictionary(p => p.Key, p => p.Value));
        }
    }

    [Fact]
    public async Task Route_enforces_review_tenancy_and_bucket_range()
    {
        using var host = new TestHost();
        using var reviewer = host.ClientAs(TestPrincipals.AcmeReviewerKey);
        using var sender = host.ClientAs(TestPrincipals.AcmeSenderKey);
        using var anonymous = host.Anonymous();
        const string route = "/v1/proxy/connection-outcomes";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.GetAsync(route)).StatusCode);
        var counters = host.Services.GetRequiredService<ProxyConnectionOutcomes>();
        counters.Count(TestPrincipals.AcmeTenant, ProxyListenerProtocol.Imap, ProxyConnectionReason.TlsHandshakeTimeout);
        counters.Count(TestPrincipals.GlobexTenant, ProxyListenerProtocol.Imap, ProxyConnectionReason.GlobalSessionLimitReached);
        await counters.FlushAsync();
        var result = await reviewer.GetFromJsonAsync<ProxyConnectionOutcomesResponse>(route);
        Assert.NotNull(result);
        Assert.Equal(TestPrincipals.AcmeTenant, result.TenantId);
        Assert.Equal("TlsHandshakeTimeout", Assert.Single(result.Buckets).Reason);
        foreach (var offset in new[] { -8, 1 })
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(ProxyConnectionOutcomes.Bucket(host.Services.GetRequiredService<TimeProvider>().GetUtcNow())).AddDays(offset);
            Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.GetAsync(route + "?from=" + Uri.EscapeDataString(date.ToString("O")))).StatusCode);
        }
        var unaligned = DateTimeOffset.FromUnixTimeSeconds(ProxyConnectionOutcomes.Bucket(host.Services.GetRequiredService<TimeProvider>().GetUtcNow())).AddSeconds(1);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.GetAsync(route + "?through=" + Uri.EscapeDataString(unaligned.ToString("O")))).StatusCode);
    }
}
