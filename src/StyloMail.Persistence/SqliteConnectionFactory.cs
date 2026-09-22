using Microsoft.Data.Sqlite;

namespace StyloMail.Persistence;

/// <summary>
/// Opens SQLite connections with the native <c>vec0</c> extension loaded.
/// </summary>
/// <remarks>
/// SQLite loads extensions per connection, not per database, so every connection must load it.
/// A store that works on one connection and silently fails on the next is a class of bug that
/// only shows up under concurrency, so loading happens here rather than at call sites.
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Opens a connection with the vector extension loaded and foreign keys enforced.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // SQLite defaults foreign_keys OFF; a profile/centroid mismatch that should be
        // impossible becomes possible without this.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        SqliteVectorExtension.EnsureLoaded(connection);
        return connection;
    }
}

/// <summary>Loads the native <c>vec0</c> extension and reports its version.</summary>
public static class SqliteVectorExtension
{
    internal const string ModuleName = "vec0";

    private static readonly Lock Gate = new();
    private static string? _cachedVersion;

    /// <summary>
    /// Loads <c>vec0</c> onto this connection. Throws rather than degrading, because a store
    /// that quietly lacks vector search would produce silently worse near-duplicate detection —
    /// exactly the failure mode that lets a changed bank account slip past the reuse gate.
    /// </summary>
    internal static void EnsureLoaded(SqliteConnection connection)
    {
        try
        {
            connection.LoadExtension(ModuleName);
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                $"The native SQLite extension '{ModuleName}' could not be loaded. Vector search requires " +
                "the sqlite-vec native library for this platform (osx-arm64, osx-x64, linux-x64, " +
                "linux-arm64 or win-x64); check that its runtime asset was copied to the output " +
                "directory.", ex);
        }
    }

    /// <summary>Version string reported by <c>vec_version()</c>. Cached after the first successful probe.</summary>
    public static string Version(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_cachedVersion is not null)
            {
                return _cachedVersion;
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vec_version();";
        var version = cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException("vec_version() returned no value.");

        lock (Gate)
        {
            _cachedVersion = version;
        }

        return version;
    }
}
