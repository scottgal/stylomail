using Microsoft.Data.Sqlite;
using StyloMail.Core;

namespace StyloMail.Persistence;

/// <summary>A profile whose semantic centroid is near a query vector.</summary>
public sealed record CentroidMatch
{
    public required string TenantId { get; init; }

    public required string ProfileScope { get; init; }

    public required string ProfileKey { get; init; }

    /// <summary>Distance reported by the vector index. Lower is closer.</summary>
    public required double Distance { get; init; }
}

/// <summary>
/// Stores each profile's semantic centroid as a vector and answers nearest-neighbour queries.
/// </summary>
/// <remarks>
/// The centroid is the profile's mean position across the semantic dimensions, which is what
/// lets drift be measured against a stable representation rather than compared message-to-message.
///
/// <para>
/// <b>Search is tenant-partitioned at the index level</b>, so a query for one tenant can never
/// return another tenant's profile. Cross-tenant sharing is off by default; if it is ever enabled
/// it must be an explicit, separately-evaluated path, not a side effect of an unfiltered query.
/// </para>
/// </remarks>
public sealed class SemanticCentroidStore
{
    private readonly SqliteConnectionFactory _factory;

    public SemanticCentroidStore(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Creates or replaces a profile's centroid, keeping the vector index in step.</summary>
    public long Upsert(
        string tenantId,
        string profileScope,
        string profileKey,
        ReadOnlySpan<float> embedding,
        string dimensionSchemaVersion,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionSchemaVersion);

        if (embedding.Length != SqliteSchema.SemanticDimensionCount)
        {
            throw new ArgumentException(
                $"Expected {SqliteSchema.SemanticDimensionCount} dimensions, got {embedding.Length}.",
                nameof(embedding));
        }

        var tenantScope = BuildTenantScope(tenantId, profileScope);
        var vector = ToVectorLiteral(embedding);
        var stamp = updatedAt.ToUniversalTime().ToString("O");

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        // A centroid implies a profile. Rather than dropping the foreign key, create a bare
        // profile row when none exists, which also models cold start honestly: the profile is
        // present with no trusted baseline yet, rather than absent and indistinguishable from
        // "never seen".
        using (var ensureProfile = connection.CreateCommand())
        {
            ensureProfile.Transaction = transaction;
            ensureProfile.CommandText =
                """
                INSERT OR IGNORE INTO profiles
                    (tenant_id, profile_scope, profile_key, direction, updated_at)
                VALUES ($tenant, $scope, $key, NULL, $at);
                """;
            ensureProfile.Parameters.AddWithValue("$tenant", tenantId);
            ensureProfile.Parameters.AddWithValue("$scope", profileScope);
            ensureProfile.Parameters.AddWithValue("$key", profileKey);
            ensureProfile.Parameters.AddWithValue("$at", stamp);
            ensureProfile.ExecuteNonQuery();
        }

        long centroidId;
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO semantic_centroid
                    (tenant_id, profile_scope, profile_key, dimension_schema_version, updated_at)
                VALUES ($tenant, $scope, $key, $version, $at)
                ON CONFLICT (tenant_id, profile_scope, profile_key, dimension_schema_version)
                DO UPDATE SET updated_at = excluded.updated_at
                RETURNING centroid_id;
                """;
            upsert.Parameters.AddWithValue("$tenant", tenantId);
            upsert.Parameters.AddWithValue("$scope", profileScope);
            upsert.Parameters.AddWithValue("$key", profileKey);
            upsert.Parameters.AddWithValue("$version", dimensionSchemaVersion);
            upsert.Parameters.AddWithValue("$at", stamp);

            centroidId = Convert.ToInt64(upsert.ExecuteScalar());
        }

        // vec0 has no UPSERT; delete-then-insert is the supported way to replace a row.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM semantic_centroid_vec WHERE centroid_id = $id;";
            delete.Parameters.AddWithValue("$id", centroidId);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO semantic_centroid_vec (centroid_id, tenant_scope, embedding)
                VALUES ($id, $scope, $embedding);
                """;
            insert.Parameters.AddWithValue("$id", centroidId);
            insert.Parameters.AddWithValue("$scope", tenantScope);
            insert.Parameters.AddWithValue("$embedding", vector);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return centroidId;
    }

    /// <summary>
    /// Returns the <paramref name="k"/> nearest profiles for one tenant and scope.
    /// An empty result means no profile is close, which callers must treat as a cold-start
    /// condition rather than as evidence of normality.
    /// </summary>
    public IReadOnlyList<CentroidMatch> FindNearest(
        string tenantId,
        string profileScope,
        ReadOnlySpan<float> embedding,
        int k,
        string dimensionSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        if (embedding.Length != SqliteSchema.SemanticDimensionCount)
        {
            throw new ArgumentException(
                $"Expected {SqliteSchema.SemanticDimensionCount} dimensions, got {embedding.Length}.",
                nameof(embedding));
        }

        var tenantScope = BuildTenantScope(tenantId, profileScope);

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT c.tenant_id, c.profile_scope, c.profile_key, v.distance
            FROM semantic_centroid_vec v
            JOIN semantic_centroid c ON c.centroid_id = v.centroid_id
            WHERE v.embedding MATCH $embedding
              AND v.k = $k
              AND v.tenant_scope = $scope
              AND c.dimension_schema_version = $version
            ORDER BY v.distance;
            """;
        cmd.Parameters.AddWithValue("$embedding", ToVectorLiteral(embedding));
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$scope", tenantScope);
        cmd.Parameters.AddWithValue("$version", dimensionSchemaVersion);

        var matches = new List<CentroidMatch>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(new CentroidMatch
            {
                TenantId = reader.GetString(0),
                ProfileScope = reader.GetString(1),
                ProfileKey = reader.GetString(2),
                Distance = reader.GetDouble(3),
            });
        }

        return matches;
    }

    /// <summary>Removes a profile's centroid, e.g. on profile eviction or data-subject deletion.</summary>
    public void Delete(string tenantId, string profileScope, string profileKey)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();

        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                "SELECT centroid_id FROM semantic_centroid WHERE tenant_id = $t AND profile_scope = $s AND profile_key = $k;";
            find.Parameters.AddWithValue("$t", tenantId);
            find.Parameters.AddWithValue("$s", profileScope);
            find.Parameters.AddWithValue("$k", profileKey);

            var ids = new List<long>();
            using (var reader = find.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            foreach (var id in ids)
            {
                using var removeVec = connection.CreateCommand();
                removeVec.Transaction = transaction;
                removeVec.CommandText = "DELETE FROM semantic_centroid_vec WHERE centroid_id = $id;";
                removeVec.Parameters.AddWithValue("$id", id);
                removeVec.ExecuteNonQuery();
            }
        }

        using (var removeRows = connection.CreateCommand())
        {
            removeRows.Transaction = transaction;
            removeRows.CommandText =
                "DELETE FROM semantic_centroid WHERE tenant_id = $t AND profile_scope = $s AND profile_key = $k;";
            removeRows.Parameters.AddWithValue("$t", tenantId);
            removeRows.Parameters.AddWithValue("$s", profileScope);
            removeRows.Parameters.AddWithValue("$k", profileKey);
            removeRows.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Tenant and scope are combined into one indexable value so a single metadata filter
    /// enforces both isolation dimensions.
    /// </summary>
    private static string BuildTenantScope(string tenantId, string profileScope) =>
        $"{tenantId}{profileScope}";

    private static string ToVectorLiteral(ReadOnlySpan<float> embedding)
    {
        var builder = new System.Text.StringBuilder(embedding.Length * 8);
        builder.Append('[');
        for (var i = 0; i < embedding.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(embedding[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }
}
