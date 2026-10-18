using Microsoft.Extensions.Options;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// The one-time evidence index backfill: atomic, explicitly marked, resumable, and conservative
/// about duplicate claims.
/// </summary>
/// <remarks>
/// These tests drive <see cref="HostDatabase"/> directly rather than a web host, because the
/// property under test is the migration itself: a crash between the first inserted claim and the
/// marker must leave neither, and the next startup must rebuild.
/// </remarks>
public sealed class PropertyEvidenceMigrationTests
{
    private const string Tenant = "acme";

    [Fact]
    public void A_crashed_backfill_rolls_back_and_resumes()
    {
        var (database, directory) = CreateDatabase();
        try
        {
            database.EnsureCreated();
            Seed(database, "e1", "role", """[{"id":"ref-1","kind":"Message"}]""");
            Seed(database, "e2", "role", """[{"id":"ref-2","kind":"Message"}]""");

            ResetMarker(database);

            Assert.Throws<InvalidOperationException>(() =>
                database.EnsureCreated(() => throw new InvalidOperationException("injected")));

            // The transaction rolled back: no partial claim and no marker.
            Assert.Equal(0, CountEvidence(database));
            Assert.Equal(0, CountMarker(database));

            database.EnsureCreated();

            Assert.Equal(2, CountEvidence(database));
            Assert.Equal(1, CountMarker(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_partial_pre_marker_index_is_rebuilt_not_trusted()
    {
        var (database, directory) = CreateDatabase();
        try
        {
            database.EnsureCreated();
            Seed(database, "e1", "role", """[{"id":"ref-1","kind":"Message"}]""");
            Seed(database, "e2", "role", """[{"id":"ref-2","kind":"Message"}]""");

            ResetMarker(database);

            // Simulate a build that inserted one claim and then died before the marker existed.
            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO host_entity_property_evidence
                        (tenant_id, scope_kind, scope_id, property_key, ref_kind, ref_id,
                         access_outcome, retention)
                    VALUES ('acme', 'Entity', 'e1', 'role', 'Message', 'ref-1', 'Available', 'Unknown');
                    """;
                command.ExecuteNonQuery();
            }

            database.EnsureCreated();

            Assert.Equal(2, CountEvidence(database));
            Assert.Equal(1, CountMarker(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_empty_index_with_the_marker_is_not_rebuilt()
    {
        var (database, directory) = CreateDatabase();
        try
        {
            database.EnsureCreated();
            Seed(database, "e1", "role", """[{"id":"ref-1","kind":"Message"}]""");
            ResetMarker(database);
            database.EnsureCreated();

            Assert.Equal(1, CountEvidence(database));

            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM host_entity_property_evidence;";
                command.ExecuteNonQuery();
            }

            database.EnsureCreated();

            // The marker is the completion record; an empty index after it is not a reason to
            // rescan every property on every startup.
            Assert.Equal(0, CountEvidence(database));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_claims_are_merged_conservatively_in_either_order()
    {
        var (database, directory) = CreateDatabase();
        try
        {
            database.EnsureCreated();

            Seed(
                database,
                "e1",
                "role",
                """[{"id":"dup","kind":"Message","accessOutcome":"Available"},{"id":"dup","kind":"Message","accessOutcome":"Withheld"}]""");
            Seed(
                database,
                "e2",
                "role",
                """[{"id":"dup","kind":"Message","accessOutcome":"Withheld"},{"id":"dup","kind":"Message","accessOutcome":"Available"}]""");

            ResetMarker(database);
            database.EnsureCreated();

            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT scope_id, access_outcome, COUNT(*)
                FROM host_entity_property_evidence
                WHERE tenant_id = 'acme' AND ref_id = 'dup'
                GROUP BY scope_id, access_outcome
                ORDER BY scope_id;
                """;

            var rows = new List<(string Scope, string Access, int Count)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }

            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal("Withheld", row.Access);
                Assert.Equal(1, row.Count);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_stored_observed_time_survives_the_backfill()
    {
        var (database, directory) = CreateDatabase();
        try
        {
            var at = new DateTimeOffset(2025, 12, 1, 10, 30, 0, TimeSpan.Zero);
            database.EnsureCreated();

            Seed(
                database,
                "e1",
                "role",
                """[{"id":"timed","kind":"Message","observedAt":"2025-12-01T10:30:00+00:00"}]""");

            ResetMarker(database);
            database.EnsureCreated();

            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT observed_at FROM host_entity_property_evidence "
                + "WHERE tenant_id = 'acme' AND ref_id = 'timed';";

            var stored = command.ExecuteScalar() as string;
            Assert.NotNull(stored);
            Assert.Equal(at, DateTimeOffset.Parse(stored!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static (HostDatabase Database, string Directory) CreateDatabase()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "stylomail-evidence-migration-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var database = new HostDatabase(Options.Create(new HostStorageOptions
        {
            DatabasePath = Path.Combine(directory, "host.db"),
        }));

        return (database, directory);
    }

    private static void Seed(HostDatabase database, string scopeId, string key, string refs)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO host_entity_property
                (tenant_id, scope_kind, scope_id, property_key, value, source, evidence_refs,
                 set_by, set_at)
            VALUES ($tenant, 'Entity', $scope, $key, 'vendor', 'Declared', $refs,
                    'test', $at);
            """;
        command.Parameters.AddWithValue("$tenant", Tenant);
        command.Parameters.AddWithValue("$scope", scopeId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$refs", refs);
        command.Parameters.AddWithValue("$at", "2026-01-01T00:00:00.0000000Z");
        command.ExecuteNonQuery();
    }

    private static void ResetMarker(HostDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM host_schema_migration WHERE name = $name;";
        command.Parameters.AddWithValue("$name", HostDatabase.PropertyEvidenceMigrationName);
        command.ExecuteNonQuery();
    }

    private static int CountEvidence(HostDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_entity_property_evidence;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountProperties(HostDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_entity_property;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountMarker(HostDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM host_schema_migration WHERE name = $name;";
        command.Parameters.AddWithValue("$name", HostDatabase.PropertyEvidenceMigrationName);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
