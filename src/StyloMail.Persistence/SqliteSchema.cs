using Microsoft.Data.Sqlite;

namespace StyloMail.Persistence;

/// <summary>
/// Creates and versions the StyloMail SQLite schema.
/// </summary>
/// <remarks>
/// Everything is tenant-scoped. Profile keys are tenant-scoped keyed hashes rather than raw
/// addresses: email is personal data, and a store that keeps raw addresses as primary keys
/// cannot later honour a deletion request without knowing every derived copy.
/// </remarks>
public static class SqliteSchema
{
    /// <summary>Current schema version. Bump when the DDL below changes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Number of semantic dimensions the centroid vector is sized for.</summary>
    public const int SemanticDimensionCount = 12;

    /// <summary>
    /// Creates the schema if absent and returns the resulting version.
    /// Safe to call on every start.
    /// </summary>
    public static int EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // BOTH pragmas must be applied before the transaction opens. SQLite rejects each of them
        // inside one, journal_mode with "cannot change into wal mode from within a transaction",
        // synchronous with "Safety level may not be changed inside a transaction", and the failure
        // is order- and state-dependent, so it surfaced only on the *second* call against an
        // existing WAL database. That made "safe to call on every start" false and turned a
        // restart into a boot-time failure.
        using (var pragma = connection.CreateCommand())
        {
            // WAL keeps readers from blocking the delivery path. NORMAL is the usual
            // durability/throughput trade for a spool that is not the sole copy of a message.
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER NOT NULL,
                applied_at  TEXT    NOT NULL
            );
            """);

        Execute(connection, transaction, ProfileDdl);
        Execute(connection, transaction, EvidenceDdl);
        Execute(connection, transaction, CampaignDdl);
        Execute(connection, transaction, VectorDdl);

        var version = ReadVersion(connection, transaction);
        if (version == 0)
        {
            using var stamp = connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText =
                "INSERT INTO schema_version (version, applied_at) VALUES ($v, $at);";
            stamp.Parameters.AddWithValue("$v", CurrentVersion);
            stamp.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            stamp.ExecuteNonQuery();
            version = CurrentVersion;
        }

        transaction.Commit();
        return version;
    }

    private static int ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT MAX(version) FROM schema_version;";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Bounded profiles. Two distinct stores of evidence live here and must never be conflated:
    /// observed state (everything seen, including rejected traffic) and the trusted baseline
    /// (approved samples only). A sent or unreported message is not automatically trusted.
    /// </summary>
    private const string ProfileDdl =
        """
        CREATE TABLE IF NOT EXISTS profiles (
            tenant_id             TEXT    NOT NULL,
            profile_scope         TEXT    NOT NULL,  -- tenant | sender | recipient | relationship
            profile_key           TEXT    NOT NULL,  -- tenant-scoped keyed hash, never a raw address
            direction             INTEGER NULL,      -- NULL for scopes that are direction-agnostic

            -- Trusted baseline: approved samples only.
            trusted_support       INTEGER NOT NULL DEFAULT 0,
            baseline_version      INTEGER NOT NULL DEFAULT 0,
            regime_id             TEXT    NULL,
            baseline_frozen       INTEGER NOT NULL DEFAULT 0,  -- set during suspected compromise

            -- Observed state: all attempts, including rejected ones.
            observed_attempts     INTEGER NOT NULL DEFAULT 0,
            observed_recipients   INTEGER NOT NULL DEFAULT 0,
            last_observed_at      TEXT    NULL,

            -- Fast/slow trend state, serialized as JSON. Kept opaque here so the adaptive
            -- engine can evolve its window model without a migration.
            fast_average_json     TEXT    NULL,
            slow_average_json     TEXT    NULL,
            velocity_json         TEXT    NULL,
            acceleration_json     TEXT    NULL,
            bucket_state_json     TEXT    NULL,

            updated_at            TEXT    NOT NULL,
            PRIMARY KEY (tenant_id, profile_scope, profile_key)
        );

        CREATE INDEX IF NOT EXISTS ix_profiles_last_observed
            ON profiles (tenant_id, last_observed_at);
        """;

    /// <summary>Append-only decision ledger and trusted feedback. Decisions are never mutated.</summary>
    private const string EvidenceDdl =
        """
        -- PRIMARY KEY is (tenant_id, assessment_id), deliberately composite. With assessment_id
        -- alone, a lookup by id can address another tenant's row, the tenant predicate becomes a
        -- filter applied after addressing, not part of the address. Composite means a cross-tenant
        -- read cannot name a row at all, so the isolation is structural rather than a query
        -- discipline someone has to remember. (Flagged by host-, whose own ledger had this and
        -- whose guarantee would otherwise have been lost in consolidation.)
        CREATE TABLE IF NOT EXISTS decision_ledger (
            tenant_id             TEXT    NOT NULL,
            assessment_id         TEXT    NOT NULL,
            internal_message_id   TEXT    NOT NULL,
            correlation_id        TEXT    NOT NULL,   -- ours: the provider supplies no request id
            direction             INTEGER NOT NULL,

            action                INTEGER NOT NULL,
            proposed_action_shadow INTEGER NULL,
            risk_index            REAL    NOT NULL,

            policy_version        TEXT    NOT NULL,
            classifier_model_version TEXT NULL,       -- resolved id, e.g. jev-1.13.0
            question_schema_version  TEXT NOT NULL,
            preprocessing_version    TEXT NOT NULL,
            regime_id             TEXT    NULL,

            coverage_json         TEXT    NOT NULL,
            reasons_json          TEXT    NOT NULL,
            evidence_json         TEXT    NOT NULL,
            risk_dimensions_json  TEXT    NOT NULL,

            cache_hit             INTEGER NOT NULL,
            cache_key_digest      TEXT    NULL,
            cache_stale           INTEGER NOT NULL,

            assessed_at           TEXT    NOT NULL,
            PRIMARY KEY (tenant_id, assessment_id)
        );

        CREATE INDEX IF NOT EXISTS ix_decision_tenant_time
            ON decision_ledger (tenant_id, assessed_at);
        CREATE INDEX IF NOT EXISTS ix_decision_message
            ON decision_ledger (tenant_id, internal_message_id);

        CREATE TABLE IF NOT EXISTS recipient_disposition (
            assessment_id   TEXT    NOT NULL,
            tenant_id       TEXT    NOT NULL,
            recipient_key   TEXT    NOT NULL,   -- keyed hash; isolation between recipients
            recipient_risk  REAL    NOT NULL,
            action          INTEGER NOT NULL,
            delivery_state  INTEGER NOT NULL,
            re_evaluate_by  TEXT    NULL,
            PRIMARY KEY (assessment_id, recipient_key),
            FOREIGN KEY (tenant_id, assessment_id)
                REFERENCES decision_ledger (tenant_id, assessment_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS feedback (
            feedback_id     TEXT    NOT NULL PRIMARY KEY,
            tenant_id       TEXT    NOT NULL,
            assessment_id   TEXT    NOT NULL,
            scope           TEXT    NOT NULL,   -- recipient preference vs. global truth, never merged
            label           TEXT    NOT NULL,
            provenance      TEXT    NOT NULL,   -- authenticated operator | recipient | application outcome
            recorded_at     TEXT    NOT NULL,
            FOREIGN KEY (tenant_id, assessment_id)
                REFERENCES decision_ledger (tenant_id, assessment_id)
                ON DELETE CASCADE
        );
        """;

    /// <summary>Bounded rolling campaign groups for near-duplicate fan-out detection.</summary>
    private const string CampaignDdl =
        """
        CREATE TABLE IF NOT EXISTS campaign_group (
            campaign_id       TEXT    NOT NULL PRIMARY KEY,
            tenant_id         TEXT    NOT NULL,
            template_fingerprint TEXT NULL,
            link_host_set     TEXT    NULL,
            attachment_hash   TEXT    NULL,
            member_count      INTEGER NOT NULL DEFAULT 0,
            first_seen_at     TEXT    NOT NULL,
            last_seen_at      TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS campaign_member (
            campaign_id   TEXT NOT NULL,
            assessment_id TEXT NOT NULL,
            added_at      TEXT NOT NULL,
            PRIMARY KEY (campaign_id, assessment_id),
            FOREIGN KEY (campaign_id) REFERENCES campaign_group (campaign_id) ON DELETE CASCADE
        );
        """;

    /// <summary>
    /// Exact semantic cache and the vec0 centroid index.
    /// </summary>
    /// <remarks>
    /// The vector table is keyed by explicit <c>rowid</c> matching <c>centroid_id</c>, which is
    /// the form of vec0 that does not depend on how the extension version handles native
    /// non-integer primary keys.
    /// </remarks>
    private const string VectorDdl =
        """
        CREATE TABLE IF NOT EXISTS semantic_cache (
            cache_key_digest  TEXT    NOT NULL PRIMARY KEY,
            tenant_id         TEXT    NOT NULL,
            model_version     TEXT    NOT NULL,   -- resolved id, never an alias
            question_schema_version TEXT NOT NULL,
            preprocessing_version   TEXT NOT NULL,
            response_json     TEXT    NOT NULL,
            coverage_json     TEXT    NOT NULL,
            cached_at         TEXT    NOT NULL,
            expires_at        TEXT    NULL,
            last_hit_at       TEXT    NULL,
            hit_count         INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_semantic_cache_expiry
            ON semantic_cache (tenant_id, expires_at);

        CREATE TABLE IF NOT EXISTS semantic_centroid (
            centroid_id               INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id                 TEXT    NOT NULL,
            profile_scope             TEXT    NOT NULL,
            profile_key               TEXT    NOT NULL,
            dimension_schema_version  TEXT    NOT NULL,
            updated_at                TEXT    NOT NULL,
            UNIQUE (tenant_id, profile_scope, profile_key, dimension_schema_version),
            FOREIGN KEY (tenant_id, profile_scope, profile_key)
                REFERENCES profiles (tenant_id, profile_scope, profile_key) ON DELETE CASCADE
        );

        -- tenant_scope is a vec0 *metadata* column (not auxiliary `+`), so KNN queries can
        -- filter on it. Tenant isolation is enforced inside the vector search rather than by
        -- filtering results afterwards: a post-filter would let another tenant's nearest
        -- neighbours consume the k budget and silently return too few matches.
        CREATE VIRTUAL TABLE IF NOT EXISTS semantic_centroid_vec USING vec0(
            centroid_id  INTEGER PRIMARY KEY,
            tenant_scope TEXT,
            embedding    FLOAT[12]
        );
        """;
}
