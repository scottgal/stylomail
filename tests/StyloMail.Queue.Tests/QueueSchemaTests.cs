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
