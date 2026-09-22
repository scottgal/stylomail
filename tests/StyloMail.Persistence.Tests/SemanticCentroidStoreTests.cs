using Microsoft.Data.Sqlite;
using StyloMail.Core;

namespace StyloMail.Persistence.Tests;

public sealed class SemanticCentroidStoreTests : IDisposable
{
    private const string Version = SemanticDimensions.QuestionSchemaVersion;
    private const string Scope = "relationship";

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SemanticCentroidStore _store;
    private readonly DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public SemanticCentroidStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"stylomail-test-{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_dbPath);

        using var connection = _factory.Open();
        SqliteSchema.EnsureCreated(connection);

        _store = new SemanticCentroidStore(_factory);
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
    public void Schema_is_created_at_the_current_version()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_version;";

        Assert.Equal(SqliteSchema.CurrentVersion, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    /// <summary>
    /// Guards against the vector width and the question set drifting apart. If someone adds a
    /// thirteenth dimension, embeddings would be silently truncated or rejected and the
    /// failure would surface as mysteriously poor matching rather than an error.
    /// </summary>
    [Fact]
    public void Vector_width_matches_the_core_question_set()
    {
        Assert.Equal(SqliteSchema.SemanticDimensionCount, SemanticDimensions.All.Count);
    }

    [Fact]
    public void Nearest_returns_the_closest_profile_first()
    {
        _store.Upsert("tenant-a", Scope, "far", BuildVector(0.0f), Version, _now);
        _store.Upsert("tenant-a", Scope, "near", BuildVector(0.95f), Version, _now);

        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 2, Version);

        Assert.Equal(2, matches.Count);
        Assert.Equal("near", matches[0].ProfileKey);
        Assert.Equal("far", matches[1].ProfileKey);
        Assert.True(matches[0].Distance < matches[1].Distance);
    }

    /// <summary>
    /// Cross-tenant leakage is a security failure, not a ranking bug. Tenant B holds a profile
    /// *closer* to the query than anything tenant A owns; A's search must still return only A's.
    /// </summary>
    [Fact]
    public void Search_never_crosses_a_tenant_boundary()
    {
        _store.Upsert("tenant-b", Scope, "b-exact-match", BuildVector(1.0f), Version, _now);
        _store.Upsert("tenant-a", Scope, "a-distant", BuildVector(0.1f), Version, _now);

        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 10, Version);

        Assert.Single(matches);
        Assert.Equal("a-distant", matches[0].ProfileKey);
        Assert.DoesNotContain(matches, m => m.TenantId == "tenant-b");
    }

    [Fact]
    public void Search_never_crosses_a_profile_scope()
    {
        _store.Upsert("tenant-a", "sender", "other-scope", BuildVector(1.0f), Version, _now);
        _store.Upsert("tenant-a", Scope, "right-scope", BuildVector(0.2f), Version, _now);

        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 10, Version);

        Assert.Single(matches);
        Assert.Equal("right-scope", matches[0].ProfileKey);
    }

    [Fact]
    public void Upsert_replaces_an_existing_centroid_rather_than_duplicating_it()
    {
        _store.Upsert("tenant-a", Scope, "sender-1", BuildVector(0.0f), Version, _now);
        _store.Upsert("tenant-a", Scope, "sender-1", BuildVector(1.0f), Version, _now);

        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 10, Version);

        Assert.Single(matches);
        Assert.Equal("sender-1", matches[0].ProfileKey);
    }

    [Fact]
    public void Delete_removes_the_centroid_from_the_index()
    {
        _store.Upsert("tenant-a", Scope, "sender-1", BuildVector(1.0f), Version, _now);
        _store.Delete("tenant-a", Scope, "sender-1");

        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 10, Version);

        Assert.Empty(matches);
    }

    [Fact]
    public void An_unknown_profile_yields_no_match_rather_than_a_false_negative()
    {
        var matches = _store.FindNearest("tenant-a", Scope, BuildVector(1.0f), k: 5, Version);

        // Cold start is an empty result, never a silent zero-distance match.
        Assert.Empty(matches);
    }

    [Fact]
    public void A_wrong_width_embedding_is_rejected()
    {
        var tooShort = new float[SqliteSchema.SemanticDimensionCount - 1];

        Assert.Throws<ArgumentException>(() =>
            _store.Upsert("tenant-a", Scope, "sender-1", tooShort, Version, _now));
    }

    private static float[] BuildVector(float first, float rest = 0.0f)
    {
        var vector = new float[SqliteSchema.SemanticDimensionCount];
        vector[0] = first;
        for (var i = 1; i < vector.Length; i++)
        {
            vector[i] = rest;
        }

        return vector;
    }
}
