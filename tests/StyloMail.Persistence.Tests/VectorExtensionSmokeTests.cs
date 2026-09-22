using Microsoft.Data.Sqlite;

namespace StyloMail.Persistence.Tests;

/// <summary>
/// Verifies that the native <c>vec0</c> extension actually loads and answers a KNN query on
/// this machine. A package reference restoring successfully proves nothing about whether the
/// native binary is present, correctly architected, and loadable, this test is the difference
/// between "the dependency is declared" and "vector search works".
/// </summary>
public sealed class VectorExtensionSmokeTests
{
    [Fact]
    public void Vec0_extension_loads_and_answers_a_knn_query()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.LoadExtension("vec0");

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE vec_items USING vec0(embedding FLOAT[4]);";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO vec_items(rowid, embedding) VALUES
                    (1, '[1.0, 0.0, 0.0, 0.0]'),
                    (2, '[0.0, 1.0, 0.0, 0.0]'),
                    (3, '[0.9, 0.1, 0.0, 0.0]');
                """;
            insert.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText =
            """
            SELECT rowid, distance FROM vec_items
            WHERE embedding MATCH '[1.0, 0.0, 0.0, 0.0]' AND k = 2
            ORDER BY distance;
            """;

        using var reader = query.ExecuteReader();
        var nearest = new List<long>();
        while (reader.Read())
        {
            nearest.Add(reader.GetInt64(0));
        }

        // Row 1 is the query vector itself; row 3 is the next closest by cosine/L2.
        Assert.Equal([1L, 3L], nearest);
    }

    [Fact]
    public void Vec0_extension_reports_its_version()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.LoadExtension("vec0");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vec_version();";

        var version = cmd.ExecuteScalar() as string;

        Assert.False(string.IsNullOrWhiteSpace(version), "vec_version() returned nothing.");
        Assert.StartsWith("v0.1.", version);
    }
}
