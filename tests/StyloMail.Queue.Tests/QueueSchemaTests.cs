using Microsoft.Data.Sqlite;
using StyloMail.Core;

namespace StyloMail.Queue.Tests;

/// <summary>
/// Schema properties that only an <em>insert</em> can prove.
/// </summary>
/// <remarks>
/// Raised by `overview-` after a composite-primary-key change silently invalidated two foreign keys:
/// SQLite accepts a foreign key to a non-unique column at create time and only rejects it on the
/// first write, so a schema test suite that only ever creates passes while the constraint is broken.
/// The same shape as every other toothless test, it cannot fail.
/// </remarks>
public class QueueSchemaTests
{
    [Fact]
    public async Task Foreign_keys_are_enforced_on_the_connections_the_store_uses()
    {
        using var h = new QueueHarness();
        await h.Store.InitializeAsync();

        // Every writer in this suite inserts recipients that reference a real item, so if foreign
        // keys were OFF every test here would still pass and the constraint would be decorative.
        // This is the one insert that can only succeed if enforcement is real.
        await using var connection = h.Connections.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO queue_recipient (queue_id, recipient_key, state, attempts)
            VALUES ('q-does-not-exist', 'orphan@example.test', 0, 0);
            """;

        var error = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_review_deadline_and_audit_table_are_part_of_the_schema()
    {
        using var h = new QueueHarness();
        await h.Store.InitializeAsync();

        await using var connection = h.Connections.Open();

        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(queue_recipient);";
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await columns.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                present.Add(reader.GetString(1));
            }

            Assert.Contains("review_deadline", present);
        }

        await using (var table = connection.CreateCommand())
        {
            table.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'queue_review_event';";
            Assert.Equal(1L, (long)(await table.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task A_v6_opening_table_migrates_to_tagged_pack_and_preserves_rows()
    {
        using var h = new QueueHarness();
        await h.Store.InitializeAsync();

        await using (var connection = h.Connections.Open())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE queue_review_opening;
                CREATE TABLE queue_review_opening (
                    tenant_id TEXT NOT NULL,
                    queue_id TEXT NOT NULL,
                    opening_event_id TEXT NOT NULL,
                    assessment_id TEXT NOT NULL,
                    pack_id TEXT NOT NULL,
                    pack_version TEXT NOT NULL,
                    rule_set_id TEXT NOT NULL,
                    rule_set_version TEXT NOT NULL,
                    rule_id TEXT NULL,
                    rule_version INTEGER NULL,
                    reason_code TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    evidence_digest TEXT NULL,
                    decided_at TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, queue_id)
                );
                INSERT INTO queue_review_opening
                    (tenant_id, queue_id, opening_event_id, assessment_id, pack_id, pack_version,
                     rule_set_id, rule_set_version, rule_id, rule_version, reason_code, reason,
                     evidence_digest, decided_at)
                VALUES ('acme', 'q-old', 'evt-old', 'asm-old', 'pack-commerce', '1.0.0',
                        'rules-commerce', 'rules/1', 'rule-payment', 1, 'policy.review',
                        'legacy opening', 'digest-old', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO queue_review_opening
                    (tenant_id, queue_id, opening_event_id, assessment_id, pack_id, pack_version,
                     rule_set_id, rule_set_version, rule_id, rule_version, reason_code, reason,
                     evidence_digest, decided_at)
                VALUES ('acme', 'q-old-null', 'evt-old-2', 'asm-old-2', 'pack-commerce', '1.0.0',
                        'rules-commerce', 'rules/1', 'rule-payment', 1, 'policy.review',
                        'legacy opening without digest', NULL, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO queue_schema_version (version, applied_at)
                    VALUES (6, '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var reopened = h.Reopen(clock => new QueueOptions { TimeProvider = clock });
        await reopened.InitializeAsync();

        await using (var connection = h.Connections.Open())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT queue_id, opening_source, pack_id, pack_version, legacy_digest_absent
                  FROM queue_review_opening
                 WHERE queue_id IN ('q-old', 'q-old-null')
                 ORDER BY queue_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("q-old", reader.GetString(0));
            Assert.Equal("pack", reader.GetString(1));
            Assert.Equal("pack-commerce", reader.GetString(2));
            Assert.Equal("1.0.0", reader.GetString(3));
            Assert.Equal(0, reader.GetInt32(4));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("q-old-null", reader.GetString(0));
            Assert.Equal("pack", reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(4));
        }
    }

    [Fact]
    public async Task A_tagged_v7_opening_table_without_the_flag_gains_it_on_open()
    {
        using var h = new QueueHarness();
        await h.Store.InitializeAsync();

        await using (var connection = h.Connections.Open())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE queue_review_opening;
                CREATE TABLE queue_review_opening (
                    tenant_id TEXT NOT NULL,
                    queue_id TEXT NOT NULL,
                    opening_event_id TEXT NOT NULL,
                    assessment_id TEXT NOT NULL,
                    opening_source TEXT NOT NULL,
                    pack_id TEXT NULL,
                    pack_version TEXT NULL,
                    rule_set_id TEXT NULL,
                    rule_set_version TEXT NULL,
                    rule_id TEXT NULL,
                    rule_version INTEGER NULL,
                    reason_code TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    evidence_digest TEXT NULL,
                    unavailable_reason TEXT NULL,
                    fallback_deadline TEXT NULL,
                    decided_at TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, queue_id)
                );
                INSERT INTO queue_review_opening
                    (tenant_id, queue_id, opening_event_id, assessment_id, opening_source,
                     pack_id, pack_version, rule_set_id, rule_set_version, reason_code, reason,
                     evidence_digest, decided_at)
                VALUES ('acme', 'q-v7', 'evt-v7', 'asm-v7', 'pack',
                        'pack-commerce', '1.0.0', 'rules-commerce', 'rules/1',
                        'policy.review', 'opening without digest', NULL,
                        '2026-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var reopened = h.Reopen(clock => new QueueOptions { TimeProvider = clock });
        await reopened.InitializeAsync();

        await using (var connection = h.Connections.Open())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT legacy_digest_absent FROM queue_review_opening WHERE queue_id = 'q-v7';";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Deleting_an_item_takes_its_recipients_and_history_with_it()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission(recipients: ["a@example.test", "b@example.test"]));

        var lease = await h.Store.ClaimNextAsync("worker-a");
        await h.Store.CompleteAsync(lease!, QueueHarness.Delivered("worker-a", "a@example.test"));

        await using var connection = h.Connections.Open();

        // Declared as ON DELETE CASCADE. Nothing in the store deletes items, so nothing else would
        // notice if the constraint pointed at the wrong column or were dropped.
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM queue_item WHERE queue_id = $id;";
            delete.Parameters.AddWithValue("$id", queueId);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        Assert.Equal(0, await CountAsync(connection, "queue_recipient", queueId));
        Assert.Equal(0, await CountAsync(connection, "queue_attempt", queueId));
    }

    [Fact]
    public async Task A_colliding_table_of_the_wrong_shape_is_detected_rather_than_adopted()
    {
        using var h = new QueueHarness();

        // Someone else's table with the same name and a different shape, exactly the collision
        // that happens in a shared SQLite file. CREATE TABLE IF NOT EXISTS silently accepts it.
        await using (var connection = h.Connections.Open())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE queue_item (id TEXT PRIMARY KEY, payload BLOB);";
            await cmd.ExecuteNonQueryAsync();
        }

        // Without a shape check this would stamp the version and then fail at the first accept with
        // a confusing "no such column", with mail on the line. It must fail here instead, where
        // refusing is still free.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.InitializeAsync());

        Assert.Contains("collision", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queue_item", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_initialisation_over_one_database_is_safe()
    {
        using var h = new QueueHarness();
        var queueId = await h.AcceptAsync(QueueHarness.Submission());

        // Two further stores over the same file, each running EnsureCreated again on a database that
        // already has the schema and a row in it.
        await h.Reopen(c => new QueueOptions { TimeProvider = c }).InitializeAsync();
        await h.Reopen(c => new QueueOptions { TimeProvider = c }).InitializeAsync();

        Assert.NotNull(await h.Store.GetItemAsync(queueId));
    }

    [Fact]
    public async Task Every_table_the_queue_creates_is_namespaced()
    {
        using var h = new QueueHarness();
        await h.Store.InitializeAsync();

        await using var connection = h.Connections.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        var tables = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // The queue shares a SQLite file with other components. An unprefixed table name is how two
        // owners end up silently adopting each other's rows through CREATE TABLE IF NOT EXISTS.
        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.StartsWith("queue_", t, StringComparison.Ordinal));
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string queueId)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE queue_id = $id;";
        cmd.Parameters.AddWithValue("$id", queueId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
