using Microsoft.Data.Sqlite;

namespace StyloMail.Persistence.Tests;

/// <summary>
/// Regression tests for "safe to call on every start".
/// </summary>
/// <remarks>
/// `EnsureCreated` originally set `PRAGMA journal_mode = WAL` inside a transaction. SQLite refuses
/// that, so the call succeeded the first time and threw the second, a boot-time failure for any
/// host that started twice against the same database, and one that only appears on restart.
/// Reported by `adaptive-`, which hit it while guarding its own startup path.
/// </remarks>
public sealed class SqliteSchemaIdempotencyTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteSchemaIdempotencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"stylomail-schema-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void EnsureCreated_can_be_called_repeatedly()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var connection = factory.Open();
            var version = SqliteSchema.EnsureCreated(connection);

            Assert.Equal(SqliteSchema.CurrentVersion, version);
        }
    }

    [Fact]
    public void EnsureCreated_is_idempotent_across_process_restart()
    {
        // The failure this guards was only visible on a *second* start against the same file, so
        // simulating restart means closing every connection and reopening the database.
        var first = new SqliteConnectionFactory(_dbPath);
        using (var connection = first.Open())
        {
            SqliteSchema.EnsureCreated(connection);
        }

        SqliteConnection.ClearAllPools();

        var second = new SqliteConnectionFactory(_dbPath);
        using var reopened = second.Open();
        var version = SqliteSchema.EnsureCreated(reopened);

        Assert.Equal(SqliteSchema.CurrentVersion, version);
    }

    [Fact]
    public void Journal_mode_is_wal_after_creation()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using var connection = factory.Open();
        SqliteSchema.EnsureCreated(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (cmd.ExecuteScalar() as string)?.ToLowerInvariant());
    }

    [Fact]
    public void Version_is_recorded_only_once()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        using var connection = factory.Open();
        SqliteSchema.EnsureCreated(connection);
        SqliteSchema.EnsureCreated(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";

        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
