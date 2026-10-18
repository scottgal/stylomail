using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Embeddings;
using StyloMail.Host.Storage;
using Xunit.Abstractions;

namespace StyloMail.Host.Tests;

public sealed class ProxyStoragePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Element_batch_binding_reuses_parameters_and_clears_nullable_values()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var create = typeof(SqliteProxyEventStore).GetMethod("CreatePointCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var command = (SqliteCommand)create.Invoke(null, [connection, null])!;
        var bind = typeof(SqliteProxyEventStore).GetMethod("BindPoint", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<SqliteCommand, ProxyElementPoint>>();
        var at = DateTimeOffset.UnixEpoch;
        var point = new ProxyElementPoint
        {
            TenantId = "tenant", AccountId = "account", MessageToken = "message", ThreadToken = "thread",
            TrafficKind = MessageTrafficKind.Retrieval, Ordinal = 0, Element = "threat",
            Scorer = ElementScorers.PrototypeKnn, ScorerVersion = ElementScorers.PrototypeKnnVersion,
            Score = 0.25, NearestExemplarId = "fixture", Availability = ProxyWarningAvailability.Available,
            AvailabilityReason = "none", ModelTag = "model", PrefixVersion = "prefix", Dimension = 256,
            VocabularyVersion = ElementVocabulary.Version, ObservedAt = at,
        };
        var absent = point with
        {
            Ordinal = 1, Score = null, NearestExemplarId = null,
            Availability = ProxyWarningAvailability.NotProduced, AvailabilityReason = "no_exemplars",
        };
        for (var i = 0; i < 100; i++) bind(command, point);
        var parameters = command.Parameters.Cast<SqliteParameter>().ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) bind(command, i % 2 == 0 ? point : absent);
        var perRow = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        output.WriteLine($"Bytes allocated per point binding: {perRow}");
        Assert.True(perRow < 512, $"Point binding allocated {perRow} bytes per row");
        Assert.Equal(18, command.Parameters.Count);
        for (var i = 0; i < parameters.Length; i++) Assert.Same(parameters[i], command.Parameters[i]);
        Assert.Equal(DBNull.Value, command.Parameters["$score"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["$exemplar"].Value);
        Assert.Equal("not_produced", command.Parameters["$availability"].Value);
        Assert.Equal("no_exemplars", command.Parameters["$reason"].Value);
        Assert.Equal(1, command.Parameters["$ordinal"].Value);
        bind(command, point);
        Assert.Equal(0.25, command.Parameters["$score"].Value);
        Assert.Equal("fixture", command.Parameters["$exemplar"].Value);
        Assert.Equal("available", command.Parameters["$availability"].Value);
    }

    [Fact]
    public async Task Session_window_counters_use_a_covering_kind_and_time_range_index()
    {
        var directory = Path.Combine(Path.GetTempPath(), "proxy-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new HostDatabase(Options.Create(new HostStorageOptions { DatabasePath = Path.Combine(directory, "host.db") }));
            database.EnsureCreated();
            using var connection = database.Open();
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<9999)
                    INSERT INTO proxy_session_event
                    (tenant_id,session_id,protocol,direction,kind,occurred_at,client_bytes,backend_bytes,bound_reason)
                    SELECT CASE WHEN i%2=0 THEN 'tenant' ELSE 'other' END, 'session-'||i,
                           'Imap','Retrieval',CASE WHEN i%3=0 THEN 'Opened' ELSE 'Closed' END,
                           CASE WHEN i%5=0 THEN '2026-10-18T00:00:00.0000000+00:00' ELSE '2026-10-01T00:00:00.0000000+00:00' END,
                           0,0,'None' FROM n;
                    """;
                seed.ExecuteNonQuery();
            }

            var from = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
            var store = new SqliteProxyEventStore(database);
            var indexed = await store.CountAsync("tenant", from, from.AddDays(1), CancellationToken.None);
            var plan = Plan();
            output.WriteLine(plan);
            Assert.Contains("USING COVERING INDEX", plan);
            Assert.Contains("kind=?", plan);
            Assert.Contains("occurred_at>? AND occurred_at<?", plan);
            Assert.Equal(334L, indexed.Opened);
            Assert.Equal(666L, indexed.Closed);

            // The control proves the planner guard detects the old schema, and verifies that the
            // index changes the access path without changing tenant/time scoped counter values.
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP INDEX ix_proxy_session_event_kind_time;";
            drop.ExecuteNonQuery();
            Assert.DoesNotContain("kind=?", Plan());
            Assert.Equal(indexed, await store.CountAsync("tenant", from, from.AddDays(1), CancellationToken.None));

            // EnsureCreated is the upgrade path for an existing database as well as a fresh one.
            database.EnsureCreated();
            Assert.Contains("kind=?", Plan());

            string Plan()
            {
                // EXPLAIN can reuse a pooled connection's cached plan after cross-connection DDL.
                // Open a new physical connection for each schema arm of this planner control.
                using var planConnection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database.DatabasePath,
                    Pooling = false,
                }.ToString());
                planConnection.Open();
                using var explain = planConnection.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN SELECT COUNT(*) FROM proxy_session_event s "
                    + "WHERE s.tenant_id=$tenant AND s.kind='Opened' AND s.occurred_at >= $from AND s.occurred_at < $to;";
                explain.Parameters.AddWithValue("$tenant", "tenant");
                explain.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
                explain.Parameters.AddWithValue("$to", from.AddDays(1).ToString("O", CultureInfo.InvariantCulture));
                using var reader = explain.ExecuteReader();
                var details = new List<string>();
                while (reader.Read()) details.Add(reader.GetString(3));
                return string.Join('\n', details);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
