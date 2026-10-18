using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using StyloMail.Core;
using StyloMail.Host.Serialization;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Storage;

/// <summary>
/// The host's SQLite database: the decision ledger, the feedback record and the sender controls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The delivery queue is not here.</b> It has its own schema and its own connection policy in
/// the Queue project, and the host reaches it through <c>QueueStore</c>. That connection loads a
/// native vector extension; these tables do not need it, and keeping the ledger off that path means
/// reading a decision cannot fail because a native library was missing.
/// </para>
///
/// <para>
/// Idempotency is not here either, for the same reason it is not in a separate table: the queue
/// already scopes idempotency keys by tenant and detects a conflicting replay, and it does so in
/// the same transaction that commits the payload. A second copy of that rule in the host would be
/// a second answer to "have I already taken this?".
/// </para>
/// </remarks>
public sealed class HostDatabase
{
    private readonly string _connectionString;

    public HostDatabase(IOptions<HostStorageOptions> options)
    {
        var databasePath = options.Value.DatabasePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Busy timeout rather than immediate failure: concurrent workers legitimately contend
            // on this file, and a short wait is a better answer than a spurious outage.
            DefaultTimeout = 30,
        }.ToString();

        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // Foreign keys are off by default in SQLite, which would let a recipient row outlive
            // the queue item it belongs to.
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>Creates the host tables if they are not already present.</summary>
    public void EnsureCreated() => EnsureCreated(null);

    /// <summary>
    /// The test seam for the evidence backfill: invokes <paramref name="evidenceBackfillFault"/>
    /// after the first inserted claim, so a crash between the first insert and the migration marker
    /// can be proven to roll back and resume.
    /// </summary>
    internal void EnsureCreated(Action? evidenceBackfillFault)
    {
        using var connection = Open();

        using (var journal = connection.CreateCommand())
        {
            // Write-ahead logging: a crash mid-commit leaves a recoverable log rather than a
            // damaged database, which matters because acceptance is a durability promise.
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            journal.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = HostDdl;
            command.ExecuteNonQuery();
        }

        transaction.Commit();

        ApplyColumnMigrations(connection, evidenceBackfillFault);
    }

    /// <summary>
    /// Adds columns introduced after a database was first created.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op against a table that already exists, so a
    /// database created by an earlier build would keep its old shape and fail at the first write
    /// that names a new column, a runtime failure on startup for anyone who had already run the
    /// host. This is deliberately a small, additive migration rather than a versioning system;
    /// when the schema changes in a way this cannot express, it will need a real one.
    /// </remarks>
    private static void ApplyColumnMigrations(SqliteConnection connection, Action? evidenceBackfillFault)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(sender_control);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (column, definition) in new[]
                 {
                     ("resumed_at", "TEXT NULL"),
                     ("resumed_by", "TEXT NULL"),
                     ("resume_reason", "TEXT NULL"),
                 })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE sender_control ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }

        ApplyConversationHistoryMigration(connection);
        ApplyProxySessionAuthenticationMigration(connection);
        ApplyEntityPropertyCoverageMigration(connection);
        ApplyPropertyEvidenceMigration(connection, evidenceBackfillFault);
    }

    internal const string PropertyEvidenceMigrationName = "property-evidence-v1";

    /// <summary>
    /// One transactional rebuild of the normalised evidence table, with an explicit completion
    /// marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A nonempty index is not a completion marker.</b> A crash after the first inserted claim
    /// would otherwise leave a partial index that every later startup trusts, silently hiding
    /// claims. The backfill therefore deletes and rebuilds the whole table from the JSON rows inside
    /// one transaction, and writes <c>property-evidence-v1</c> into <c>host_schema_migration</c> in
    /// the same transaction. A failure rolls back both the rows and the marker, so the next startup
    /// resumes rather than trusting a partial state.
    /// </para>
    /// <para>
    /// Duplicates of the same <c>(kind, id)</c> within one stored array are merged conservatively:
    /// the most restrictive access outcome wins and the earliest retention instant is kept. Insert
    /// order can never make a withheld claim disappear. New writes reject duplicates instead.
    /// </para>
    /// </remarks>
    private static void ApplyPropertyEvidenceMigration(
        SqliteConnection connection,
        Action? faultAfterFirstInsert)
    {
        using (var marker = connection.CreateCommand())
        {
            marker.CommandText =
                "SELECT COUNT(*) FROM host_schema_migration WHERE name = $name;";
            marker.Parameters.AddWithValue("$name", PropertyEvidenceMigrationName);
            if (Convert.ToInt32(marker.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();

        if (!ColumnExists(connection, transaction, "host_entity_property", "evidence_invalid"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE host_entity_property ADD COLUMN evidence_invalid INTEGER NOT NULL DEFAULT 0;");
        }

        if (!ColumnExists(connection, transaction, "host_entity_property_evidence", "observed_at"))
        {
            Execute(
                connection,
                transaction,
                "ALTER TABLE host_entity_property_evidence ADD COLUMN observed_at TEXT NULL;");
        }

        // Rebuild, never resume from whatever happens to be there. This repairs a partial index
        // written by an earlier build.
        Execute(connection, transaction, "DELETE FROM host_entity_property_evidence;");

        var rows = new List<(string Tenant, string ScopeKind, string ScopeId, string Key, string? Refs)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT tenant_id, scope_kind, scope_id, property_key, evidence_refs "
                + "FROM host_entity_property;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var inserted = 0;

        foreach (var row in rows)
        {
            var references = ParseStoredEvidence(row.Refs);
            if (references is null)
            {
                MarkEvidenceInvalid(connection, transaction, row, invalid: true);
                continue;
            }

            MarkEvidenceInvalid(connection, transaction, row, invalid: false);

            foreach (var reference in MergeEvidenceClaims(references))
            {
                InsertEvidenceClaim(connection, transaction, row, reference);
                inserted++;

                if (faultAfterFirstInsert is not null && inserted == 1)
                {
                    faultAfterFirstInsert();
                }
            }
        }

        Execute(
            connection,
            transaction,
            "INSERT OR REPLACE INTO host_schema_migration (name, applied_at) VALUES ($name, $at);",
            ("$name", PropertyEvidenceMigrationName),
            ("$at", DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

        transaction.Commit();
    }

    private static List<EvidenceReference> MergeEvidenceClaims(
        IReadOnlyList<EvidenceReference> references)
    {
        var merged = new Dictionary<(EvidenceKind Kind, string Id), EvidenceReference>();

        foreach (var reference in references)
        {
            var key = (reference.Kind, reference.Id);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = reference;
                continue;
            }

            var access = MostRestrictive(existing.AccessOutcome, reference.AccessOutcome);
            var until = Earliest(existing.RetentionUntil, reference.RetentionUntil);
            var observed = Latest(existing.ObservedAt, reference.ObservedAt);
            var chosen = access == existing.AccessOutcome ? existing : reference;

            merged[key] = chosen with
            {
                AccessOutcome = access,
                Retention = until is not null ? EvidenceRetention.Scheduled : chosen.Retention,
                RetentionUntil = until,
                ObservedAt = observed,
            };
        }

        return [.. merged.Values];
    }

    private static EvidenceAccessOutcome MostRestrictive(
        EvidenceAccessOutcome left,
        EvidenceAccessOutcome right)
    {
        static int Rank(EvidenceAccessOutcome outcome) => outcome switch
        {
            EvidenceAccessOutcome.Withheld => 2,
            EvidenceAccessOutcome.NotEvaluated => 1,
            _ => 0,
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : (left <= right ? left : right);

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : (left >= right ? left : right);

    private static void MarkEvidenceInvalid(
        SqliteConnection connection,
        SqliteTransaction transaction,
        (string Tenant, string ScopeKind, string ScopeId, string Key, string? Refs) row,
        bool invalid)
    {
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText =
            "UPDATE host_entity_property SET evidence_invalid = $invalid "
            + "WHERE tenant_id = $tenant AND scope_kind = $kind AND scope_id = $id "
            + "AND property_key = $key;";
        mark.Parameters.AddWithValue("$invalid", invalid ? 1 : 0);
        mark.Parameters.AddWithValue("$tenant", row.Tenant);
        mark.Parameters.AddWithValue("$kind", row.ScopeKind);
        mark.Parameters.AddWithValue("$id", row.ScopeId);
        mark.Parameters.AddWithValue("$key", row.Key);
        mark.ExecuteNonQuery();
    }

    private static void InsertEvidenceClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        (string Tenant, string ScopeKind, string ScopeId, string Key, string? Refs) row,
        EvidenceReference reference)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR REPLACE INTO host_entity_property_evidence
                (tenant_id, scope_kind, scope_id, property_key, ref_kind, ref_id,
                 access_outcome, retention, retention_until, observed_at)
            VALUES ($tenant, $kind, $id, $key, $refKind, $refId, $access, $retention, $until, $observed);
            """;
        insert.Parameters.AddWithValue("$tenant", row.Tenant);
        insert.Parameters.AddWithValue("$kind", row.ScopeKind);
        insert.Parameters.AddWithValue("$id", row.ScopeId);
        insert.Parameters.AddWithValue("$key", row.Key);
        insert.Parameters.AddWithValue("$refKind", reference.Kind.ToString());
        insert.Parameters.AddWithValue("$refId", reference.Id);
        insert.Parameters.AddWithValue("$access", reference.AccessOutcome.ToString());
        insert.Parameters.AddWithValue("$retention", reference.Retention.ToString());
        insert.Parameters.AddWithValue(
            "$until",
            reference.RetentionUntil is { } until
                ? until.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        insert.Parameters.AddWithValue(
            "$observed",
            reference.ObservedAt is { } observed
                ? observed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Parses a stored evidence array. Null when malformed or when any element is null/empty.
    /// </summary>
    private static List<EvidenceReference>? ParseStoredEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var structured = JsonSerializer.Deserialize<List<EvidenceReference?>>(json, HostJson.Options);
            if (structured is not null)
            {
                var references = new List<EvidenceReference>(structured.Count);
                foreach (var reference in structured)
                {
                    if (reference is null
                        || string.IsNullOrWhiteSpace(reference.Id)
                        || !Enum.IsDefined(reference.Kind))
                    {
                        return null;
                    }

                    references.Add(reference);
                }

                return references;
            }
        }
        catch (JsonException)
        {
            // Legacy rows hold bare strings; fall through.
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<string?>>(json);
            if (legacy is null)
            {
                return null;
            }

            var references = new List<EvidenceReference>(legacy.Count);
            foreach (var id in legacy)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                references.Add(new EvidenceReference { Id = id, Kind = EvidenceKind.Other });
            }

            return references;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ApplyEntityPropertyCoverageMigration(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(host_entity_property);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Count == 0)
        {
            return;
        }

        foreach (var (column, definition) in new[]
                 {
                     ("coverage_analysed", "INTEGER NULL"),
                     ("coverage_total", "INTEGER NULL"),
                     ("coverage_window_start", "TEXT NULL"),
                     ("coverage_window_end", "TEXT NULL"),
                     ("coverage_unavailable_reason", "TEXT NULL"),
                 })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE host_entity_property ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds the authentication instant to a session event table created before it existed.
    /// </summary>
    /// <remarks>
    /// The sink updates an existing opened row in place, so the column has to exist on databases
    /// created by the earlier build; <c>CREATE TABLE IF NOT EXISTS</c> would silently leave them
    /// without it and every authentication update would fail.
    /// </remarks>
    private static void ApplyProxySessionAuthenticationMigration(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(proxy_session_event);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Contains("authenticated_at"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE proxy_session_event ADD COLUMN authenticated_at TEXT NULL;";
        alter.ExecuteNonQuery();
    }

    /// <summary>Adds indexed channel metadata and recovers it from legacy decision payloads.</summary>
    private static void ApplyConversationHistoryMigration(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(host_decision_ledger);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (column, definition) in new[]
                 {
                     ("channel_kind", "TEXT NULL"),
                     ("workspace_id", "TEXT NULL"),
                     ("channel_id", "TEXT NULL"),
                     ("thread_id", "TEXT NULL"),
                 })
        {
            if (existing.Contains(column))
            {
                continue;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE host_decision_ledger ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }

        BackfillConversationMetadata(connection);

        using var indexes = connection.CreateCommand();
        indexes.CommandText =
            "CREATE INDEX IF NOT EXISTS ix_host_decision_ledger_channel "
            + "ON host_decision_ledger (tenant_id, workspace_id, channel_id, recorded_at DESC, assessment_id DESC);"
            + "CREATE INDEX IF NOT EXISTS ix_host_decision_ledger_thread "
            + "ON host_decision_ledger (tenant_id, workspace_id, channel_id, thread_id, recorded_at DESC, assessment_id DESC);";
        indexes.ExecuteNonQuery();
    }

    /// <summary>
    /// Copies identifiers already present in old decision documents into queryable columns.
    /// Malformed legacy rows stay readable through the existing ledger path and remain ungroupable.
    /// </summary>
    private static void BackfillConversationMetadata(SqliteConnection connection)
    {
        var rows = new List<(string TenantId, string AssessmentId, string RecordedAt, string Payload)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT tenant_id, assessment_id, recorded_at, payload FROM host_decision_ledger "
                + "WHERE channel_kind IS NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.Payload);
                if (!document.RootElement.TryGetProperty("channel", out var channelElement))
                {
                    continue;
                }

                var channel = JsonSerializer.Deserialize<ChannelContext>(channelElement.GetRawText(), HostJson.Options);
                if (channel is null)
                {
                    continue;
                }

                // Previous ledger versions preserved the timestamp's original offset. History
                // filters and keyset ordering use canonical UTC strings, so normalize every valid
                // legacy round-trip timestamp while backfilling. If it is unparsable, preserve it
                // verbatim; the history reader will count and skip that row rather than inventing a time.
                var normalizedRecordedAt = DateTimeOffset.TryParseExact(
                    row.RecordedAt,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var recordedAt)
                    ? recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : null;

                using var update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE host_decision_ledger SET channel_kind = $kind, workspace_id = $workspace, "
                    + "channel_id = $channel, thread_id = $thread, "
                    + "recorded_at = COALESCE($recorded, recorded_at) "
                    + "WHERE tenant_id = $tenant AND assessment_id = $assessment AND channel_kind IS NULL;";
                update.Parameters.AddWithValue("$kind", channel.Kind.ToString());
                update.Parameters.AddWithValue("$workspace", (object?)channel.WorkspaceId ?? DBNull.Value);
                update.Parameters.AddWithValue("$channel", (object?)channel.ChannelId ?? DBNull.Value);
                update.Parameters.AddWithValue("$thread", (object?)channel.ThreadId ?? DBNull.Value);
                update.Parameters.AddWithValue("$recorded", (object?)normalizedRecordedAt ?? DBNull.Value);
                update.Parameters.AddWithValue("$tenant", row.TenantId);
                update.Parameters.AddWithValue("$assessment", row.AssessmentId);
                update.ExecuteNonQuery();
            }
            catch (JsonException)
            {
                // A malformed legacy payload remains visible to the ordinary listing's skipped count.
                // Do not make an unrelated valid history query fail during startup migration.
            }
        }
    }

    private const string HostDdl =
        """
        -- The explainable decision ledger. Tenancy is part of the primary key rather than a
        -- column to be filtered on, so a row belonging to another tenant cannot be addressed
        -- even by a caller who guesses a valid assessment id.
        --
        -- Named host_* deliberately. The Persistence project also declares a `decision_ledger`,
        -- normalised across columns rather than stored as a document, and the two would collide in
        -- the single database file this deployment uses, the CREATE would silently no-op against
        -- the other one and every statement here would then fail on a missing column. The prefix
        -- keeps both definitions addressable and makes the overlap visible instead of hiding it.
        CREATE TABLE IF NOT EXISTS host_decision_ledger (
            tenant_id           TEXT NOT NULL,
            assessment_id       TEXT NOT NULL,
            internal_message_id TEXT NOT NULL,
            action              TEXT NOT NULL,
            recorded_at         TEXT NOT NULL,
            channel_kind        TEXT NULL,
            workspace_id        TEXT NULL,
            channel_id          TEXT NULL,
            thread_id           TEXT NULL,
            payload             TEXT NOT NULL,
            PRIMARY KEY (tenant_id, assessment_id)
        );

        CREATE INDEX IF NOT EXISTS ix_host_decision_ledger_tenant
            ON host_decision_ledger (tenant_id, recorded_at);

        -- The join key back to a message. A reviewer goes from a quarantined message to the
        -- explanation for it by message id, so that lookup is on the console's critical path rather
        -- than an operator's occasional query, and without this it would scan the tenant's ledger.
        CREATE INDEX IF NOT EXISTS ix_host_decision_ledger_message
            ON host_decision_ledger (tenant_id, internal_message_id);

        -- Feedback is scoped: a label applies to the decision, the recipient and the scope the
        -- authorized party actually asserted, never to "this sender is fine from now on".
        -- Also host_*: see the note on host_decision_ledger above.
        CREATE TABLE IF NOT EXISTS host_feedback_record (
            feedback_id  TEXT NOT NULL PRIMARY KEY,
            tenant_id    TEXT NOT NULL,
            decision_id  TEXT NOT NULL,
            scope        TEXT NOT NULL,
            label        TEXT NOT NULL,
            recipient    TEXT NULL,
            recorded_by  TEXT NOT NULL,
            recorded_at  TEXT NOT NULL,
            note         TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_host_feedback_tenant
            ON host_feedback_record (tenant_id, decision_id);

        -- Sender controls (pause/resume). A control is a control-plane fact, recorded separately
        -- from queue state so that pausing and resuming do not resurrect or discard mail.
        --
        -- The pause fields and the resume fields are both kept. Lifting an intervention does not
        -- erase the fact that it happened: "why was this account stopped for six hours?" has to
        -- stay answerable afterwards, or the control plane cannot be audited.
        CREATE TABLE IF NOT EXISTS sender_control (
            tenant_id     TEXT NOT NULL,
            principal_id  TEXT NOT NULL,
            paused        INTEGER NOT NULL,
            paused_at     TEXT NULL,
            reason        TEXT NULL,     -- why the pause was applied
            resumed_at    TEXT NULL,
            resumed_by    TEXT NULL,
            resume_reason TEXT NULL,     -- why it was lifted
            updated_by    TEXT NOT NULL,
            updated_at    TEXT NOT NULL,
            PRIMARY KEY (tenant_id, principal_id)
        );

        -- Operator metadata about a sending principal: what a human calls it, which company it
        -- belongs to, and their own reference for it. Nothing here is read by the assessment
        -- pipeline, and `posture` and `notification_target` are stored but honoured by nothing yet:
        -- see SenderProfile for why they are carried anyway and what labels them as not-yet-acted-on.
        CREATE TABLE IF NOT EXISTS sender_profile (
            tenant_id            TEXT NOT NULL,
            principal_id         TEXT NOT NULL,
            label                TEXT NULL,
            company_id           TEXT NULL,
            notes                TEXT NULL,
            external_ref         TEXT NULL,
            notification_target  TEXT NULL,
            posture              TEXT NULL,
            updated_by           TEXT NOT NULL,
            updated_at           TEXT NOT NULL,
            PRIMARY KEY (tenant_id, principal_id)
        );

        -- Companies group senders for the console. Deliberately flat and deliberately operator-side:
        -- membership is a field on the sender rather than a list here, so promoting this to a
        -- hierarchy later is a parent link on this table and no sender moves.
        CREATE TABLE IF NOT EXISTS company (
            tenant_id   TEXT NOT NULL,
            company_id  TEXT NOT NULL,
            name        TEXT NOT NULL,
            notes       TEXT NULL,
            updated_by  TEXT NOT NULL,
            updated_at  TEXT NOT NULL,
            PRIMARY KEY (tenant_id, company_id)
        );

        -- A sender's profile is read to group the sidebar, so the join from a company back to its
        -- members is the console's common query rather than an occasional one.
        CREATE INDEX IF NOT EXISTS ix_sender_profile_company
            ON sender_profile (tenant_id, company_id);

        -- Entity properties: the policy input a conversation cannot be trusted to define.
        --
        -- One row per (tenant, scope, key). The tenant is in the primary key rather than a column to
        -- be filtered on, so a row belonging to another tenant cannot be addressed even by a caller
        -- who guesses a valid scope id. `scope_id` is opaque here: an entity is whatever the host
        -- calls a principal, a domain is a domain, a company is `company.company_id`, and a
        -- conversation is the host's thread id. Deliberately no cross-scope foreign key: inventing
        -- linkage between a domain and the people under it is exactly the re-identification the spec
        -- keeps deliberate (§9.12, invariant 4).
        --
        -- `source` and `confirmed_by` are separate facts, never one encoded in the other. The
        -- surface pill is derived from both at read time; storing it would be a second answer that
        -- can drift from the confirmation event.
        CREATE TABLE IF NOT EXISTS host_entity_property (
            tenant_id       TEXT NOT NULL,
            scope_kind      TEXT NOT NULL,
            scope_id        TEXT NOT NULL,
            property_key    TEXT NOT NULL,
            value           TEXT NOT NULL,
            source          TEXT NOT NULL,
            confidence      REAL NULL,
            model_id        TEXT NULL,
            model_version   TEXT NULL,
            prompt_contract TEXT NULL,
            spans           TEXT NULL,
            evidence_refs   TEXT NULL,
            evidence_invalid INTEGER NOT NULL DEFAULT 0,
            coverage_analysed          INTEGER NULL,
            coverage_total             INTEGER NULL,
            coverage_window_start      TEXT NULL,
            coverage_window_end        TEXT NULL,
            coverage_unavailable_reason TEXT NULL,
            set_by          TEXT NOT NULL,
            set_at          TEXT NOT NULL,
            expires_at      TEXT NULL,
            confirmed_by    TEXT NULL,
            confirmed_at    TEXT NULL,
            PRIMARY KEY (tenant_id, scope_kind, scope_id, property_key)
        );

        -- Expiry is evaluated on read, and the sweep that records it looks for rows whose instant
        -- has passed. Without this index that is a full scan of every tenant's properties.
        CREATE INDEX IF NOT EXISTS ix_host_entity_property_expiry
            ON host_entity_property (tenant_id, expires_at);

        -- Every change to a property, including an expiry. The old and new values are both carried:
        -- a clear leaves no row to join back to, and "what was it before" has to stay answerable.
        CREATE TABLE IF NOT EXISTS host_entity_property_event (
            tenant_id        TEXT NOT NULL,
            event_id         TEXT NOT NULL,
            scope_kind       TEXT NOT NULL,
            scope_id         TEXT NOT NULL,
            property_key     TEXT NOT NULL,
            action           TEXT NOT NULL,
            actor            TEXT NOT NULL,
            recorded_at      TEXT NOT NULL,
            old_value        TEXT NULL,
            new_value        TEXT NULL,
            old_source       TEXT NULL,
            new_source       TEXT NULL,
            old_confirmed_by TEXT NULL,
            new_confirmed_by TEXT NULL,
            reason           TEXT NULL,
            PRIMARY KEY (tenant_id, event_id)
        );

        -- History is read per scope and key, newest first. The key is part of the index so that one
        -- property's sequence does not scan a tenant's whole audit trail.
        CREATE INDEX IF NOT EXISTS ix_host_entity_property_event_history
            ON host_entity_property_event
               (tenant_id, scope_kind, scope_id, property_key, recorded_at DESC, event_id DESC);

        -- Normalised evidence identities. The JSON array on the property row is the contract, but
        -- citations must be found by exact (kind, id), not by a substring scan: JSON escaping
        -- (+ for +) makes the raw text an incomplete key, and a row cap over raw candidates
        -- can hide a real claim. The writer maintains this in the same transaction as the row.
        CREATE TABLE IF NOT EXISTS host_entity_property_evidence (
            tenant_id       TEXT NOT NULL,
            scope_kind      TEXT NOT NULL,
            scope_id        TEXT NOT NULL,
            property_key    TEXT NOT NULL,
            ref_kind        TEXT NOT NULL,
            ref_id          TEXT NOT NULL,
            access_outcome  TEXT NOT NULL,
            retention       TEXT NOT NULL,
            retention_until TEXT NULL,
            observed_at     TEXT NULL,
            PRIMARY KEY (tenant_id, scope_kind, scope_id, property_key, ref_kind, ref_id)
        );

        CREATE INDEX IF NOT EXISTS ix_host_entity_property_evidence_lookup
            ON host_entity_property_evidence (tenant_id, ref_kind, ref_id);

        -- Policy packs: the declared context, tolerance, ceiling and provenance that a decision is
        -- explained by. A pack is immutable once approved, and the content hash names the bytes the
        -- approval applied to rather than a version label.
        --
        -- `jurisdiction` is part of the effective key. At most one effective pack exists per tenant
        -- and jurisdiction; the partial unique index is the second line of defence behind the
        -- activation transaction, so a bug in the service cannot produce two live packs.
        CREATE TABLE IF NOT EXISTS host_policy_pack (
            tenant_id        TEXT NOT NULL,
            pack_id          TEXT NOT NULL,
            version          TEXT NOT NULL,
            state            TEXT NOT NULL CHECK(state IN ('draft','approved','effective','superseded','withdrawn')),
            jurisdiction     TEXT NOT NULL,
            content_sha256   TEXT NOT NULL,
            payload          TEXT NOT NULL,
            author           TEXT NOT NULL,
            approver         TEXT NULL,
            second_approver  TEXT NULL,
            effective_from   TEXT NOT NULL,
            review_due       TEXT NULL,
            created_at       TEXT NOT NULL,
            approved_at      TEXT NULL,
            superseded_by    TEXT NULL,
            PRIMARY KEY (tenant_id, pack_id, version)
        );

        CREATE INDEX IF NOT EXISTS ix_host_policy_pack_tenant_state
            ON host_policy_pack (tenant_id, state, effective_from);

        CREATE INDEX IF NOT EXISTS ix_host_policy_pack_due
            ON host_policy_pack (tenant_id, jurisdiction, state, effective_from);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_host_policy_pack_effective
            ON host_policy_pack (tenant_id, jurisdiction)
            WHERE state = 'effective';

        CREATE TABLE IF NOT EXISTS host_policy_pack_event (
            event_id   TEXT NOT NULL PRIMARY KEY,
            tenant_id  TEXT NOT NULL,
            pack_id    TEXT NOT NULL,
            version    TEXT NOT NULL,
            event      TEXT NOT NULL,
            actor      TEXT NOT NULL,
            reason     TEXT NOT NULL,
            at         TEXT NOT NULL,
            payload    TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_host_policy_pack_event_history
            ON host_policy_pack_event (tenant_id, pack_id, version, at DESC);

        -- Rule sets are versioned and immutable once live. A pack references a version; a tenant
        -- has one rule-set lineage, so a version must resolve to exactly one live row.
        CREATE TABLE IF NOT EXISTS host_policy_rule_set (
            tenant_id      TEXT NOT NULL,
            rule_set_id    TEXT NOT NULL,
            version        TEXT NOT NULL,
            state          TEXT NOT NULL CHECK(state IN ('candidate','live','retired')),
            content_sha256 TEXT NOT NULL,
            payload        TEXT NOT NULL,
            created_at     TEXT NOT NULL,
            promoted_at    TEXT NULL,
            approver       TEXT NULL,
            PRIMARY KEY (tenant_id, rule_set_id, version)
        );

        CREATE INDEX IF NOT EXISTS ix_host_policy_rule_set_live
            ON host_policy_rule_set (tenant_id, version, state, promoted_at DESC);

        CREATE TABLE IF NOT EXISTS host_policy_rule_event (
            event_id    TEXT NOT NULL PRIMARY KEY,
            tenant_id   TEXT NOT NULL,
            rule_set_id TEXT NOT NULL,
            version     TEXT NOT NULL,
            event       TEXT NOT NULL,
            actor       TEXT NOT NULL,
            reason      TEXT NOT NULL,
            at          TEXT NOT NULL,
            payload     TEXT NULL
        );

        -- Scoped manual exceptions. Every row is bounded: there is no unbounded binding, and
        -- revocation is a state change plus an event rather than a delete, so history survives.
        CREATE TABLE IF NOT EXISTS host_policy_binding (
            tenant_id    TEXT NOT NULL,
            binding_id   TEXT NOT NULL,
            state        TEXT NOT NULL CHECK(state IN ('active','revoked')),
            scope_kind   TEXT NOT NULL,
            scope_id     TEXT NOT NULL,
            action       TEXT NOT NULL,
            category_id  TEXT NULL,
            target_level TEXT NULL,
            starts_at    TEXT NOT NULL,
            expires_at   TEXT NOT NULL,
            review_by    TEXT NOT NULL,
            payload      TEXT NOT NULL,
            PRIMARY KEY (tenant_id, binding_id)
        );

        CREATE INDEX IF NOT EXISTS ix_host_policy_binding_scope
            ON host_policy_binding (tenant_id, scope_kind, scope_id, state, expires_at);

        CREATE TABLE IF NOT EXISTS host_policy_binding_event (
            event_id   TEXT NOT NULL PRIMARY KEY,
            tenant_id  TEXT NOT NULL,
            binding_id TEXT NOT NULL,
            event      TEXT NOT NULL,
            actor      TEXT NOT NULL,
            reason     TEXT NOT NULL,
            at         TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_host_policy_binding_event_history
            ON host_policy_binding_event (tenant_id, binding_id, at DESC);

        -- Access-proxy credentials are deliberately separate from Host API principals. The account
        -- holds a verifier only; the backend record holds ciphertext only. Login lookup is scoped
        -- to its tenant because equal client logins in different tenants must not correlate.
        CREATE TABLE IF NOT EXISTS proxy_backend_credential (
            backend_account_id TEXT NOT NULL,
            tenant_id          TEXT NOT NULL,
            provider_id        TEXT NOT NULL,
            discriminator      TEXT NOT NULL,
            protected_secret   BLOB NOT NULL,
            protection_key_id  TEXT NOT NULL,
            backend_username   TEXT NOT NULL,
            granted_scopes     TEXT NULL,
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL,
            revoked_at         TEXT NULL,
            PRIMARY KEY (tenant_id, backend_account_id),
            UNIQUE (backend_account_id, tenant_id)
        );

        CREATE TABLE IF NOT EXISTS proxy_account (
            account_id               TEXT NOT NULL PRIMARY KEY,
            tenant_id                TEXT NOT NULL,
            login_key                TEXT NOT NULL,
            password_hash_iterations INTEGER NOT NULL,
            password_hash_salt       BLOB NOT NULL,
            password_hash_digest     BLOB NOT NULL,
            backend_account_id       TEXT NOT NULL,
            disabled                 INTEGER NOT NULL,
            created_at               TEXT NOT NULL,
            updated_at               TEXT NOT NULL,
            UNIQUE (tenant_id, login_key),
            FOREIGN KEY (backend_account_id, tenant_id)
                REFERENCES proxy_backend_credential (backend_account_id, tenant_id)
        );

        -- Management telemetry for the access proxy. Access publishes these through its own event
        -- sink contract; this is the Host's durable record, not a schema the access layer reads.
        --
        -- `kind` and `protocol` are stored as names rather than ordinals: a reordered enum must never
        -- silently change the meaning of a recorded fact, and an operator reading the database should
        -- be able to tell what happened without a mapping table.
        CREATE TABLE IF NOT EXISTS proxy_connection_outcome (
            tenant_id TEXT NOT NULL,
            protocol TEXT NOT NULL CHECK(protocol IN ('Imap','Pop3','Smtp')),
            outcome TEXT NOT NULL,
            reason TEXT NOT NULL,
            bucket INTEGER NOT NULL CHECK(bucket % 300 = 0),
            count INTEGER NOT NULL CHECK(count > 0),
            CHECK((outcome='Timeout' AND reason='TlsHandshakeTimeout') OR
                  (outcome='AdmissionRejected' AND reason='GlobalSessionLimitReached')),
            PRIMARY KEY(tenant_id,protocol,outcome,reason,bucket)
        );
        CREATE TABLE IF NOT EXISTS proxy_connection_dropped (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            count INTEGER NOT NULL CHECK(count >= 0)
        );

        CREATE TABLE IF NOT EXISTS proxy_listener_event (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id    TEXT NOT NULL,
            protocol     TEXT NOT NULL,
            kind         TEXT NOT NULL,
            occurred_at  TEXT NOT NULL,
            bound_port   INTEGER NULL,
            fault_reason TEXT NOT NULL,
            safe_detail  TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_listener_event_tenant
            ON proxy_listener_event (tenant_id, occurred_at);

        -- One row per lifecycle transition, never a mutable "currently live" flag. A crash between
        -- an open and a close leaves an open row behind, which the live-session query treats as
        -- live; that is the honest reading of the last fact recorded, not a claim that a socket is
        -- still open.
        CREATE TABLE IF NOT EXISTS proxy_session_event (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id     TEXT NOT NULL,
            session_id    TEXT NOT NULL,
            account_id    TEXT NULL,
            protocol      TEXT NOT NULL,
            direction     TEXT NOT NULL,
            kind          TEXT NOT NULL,
            occurred_at   TEXT NOT NULL,
            authenticated_at TEXT NULL,
            outcome       TEXT NULL,
            client_bytes  INTEGER NOT NULL,
            backend_bytes INTEGER NOT NULL,
            bound_reason  TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_session_event_session
            ON proxy_session_event (tenant_id, session_id, id);

        -- Windowed lifecycle counters should read only the requested kind and time range.
        CREATE INDEX IF NOT EXISTS ix_proxy_session_event_kind_time
            ON proxy_session_event (tenant_id, kind, occurred_at);

        -- Structural threading facts only: non-reversible tokens for Message-ID/In-Reply-To/References,
        -- depth and time. No header value, subject, body or participant address is stored here.
        CREATE TABLE IF NOT EXISTS proxy_threading_event (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id       TEXT NOT NULL,
            account_id      TEXT NOT NULL,
            protocol        TEXT NOT NULL,
            message_token   TEXT NOT NULL,
            thread_token    TEXT NOT NULL,
            reference_depth INTEGER NOT NULL,
            occurred_at     TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_threading_event_thread
            ON proxy_threading_event (tenant_id, thread_token, occurred_at, id);

        -- One outcome row per attempted message, including a clean one. Markers alone cannot tell a
        -- measured clean message from one no analyser saw; this row is that evidence.
        CREATE TABLE IF NOT EXISTS proxy_warning_outcome (
            tenant_id           TEXT NOT NULL,
            message_token       TEXT NOT NULL,
            direction           TEXT NOT NULL,
            thread_token        TEXT NOT NULL,
            account_id          TEXT NOT NULL,
            observed_at         TEXT NOT NULL,
            availability        TEXT NOT NULL,
            availability_reason TEXT NOT NULL,
            marker_count        INTEGER NOT NULL,
            PRIMARY KEY (tenant_id, message_token, direction)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_warning_outcome_thread
            ON proxy_warning_outcome (tenant_id, thread_token, observed_at);

        -- Derived markers only: a closed kind and a bounded local finding id. No source text,
        -- header value, address or content is stored here.
        CREATE TABLE IF NOT EXISTS proxy_warning_marker (
            tenant_id           TEXT NOT NULL,
            message_token       TEXT NOT NULL,
            direction           TEXT NOT NULL,
            thread_token        TEXT NOT NULL,
            account_id          TEXT NOT NULL,
            marker_kind         TEXT NOT NULL,
            finding_id          TEXT NOT NULL,
            observed_at         TEXT NOT NULL,
            availability        TEXT NOT NULL,
            availability_reason TEXT NOT NULL,
            PRIMARY KEY (tenant_id, message_token, direction, marker_kind, finding_id)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_warning_marker_thread
            ON proxy_warning_marker (tenant_id, thread_token, observed_at);

        -- One row per attempted segmentation. Available with segment_count 0 is a measured empty
        -- message; NotProduced with a reason is unmeasured. Ordinals are the only segment identity.
        CREATE TABLE IF NOT EXISTS proxy_segment_outcome (
            tenant_id                  TEXT NOT NULL,
            message_token              TEXT NOT NULL,
            direction                  TEXT NOT NULL,
            thread_token               TEXT NOT NULL,
            account_id                 TEXT NOT NULL,
            observed_at                TEXT NOT NULL,
            availability               TEXT NOT NULL,
            availability_reason        TEXT NOT NULL,
            segment_count              INTEGER NOT NULL,
            truncated                  INTEGER NOT NULL,
            applied_max_segments       INTEGER NOT NULL,
            applied_max_segment_bytes  INTEGER NOT NULL,
            analysed_ordinals          TEXT NOT NULL,
            any_segment_dropped        INTEGER NOT NULL,
            PRIMARY KEY (tenant_id, message_token, direction)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_segment_outcome_thread
            ON proxy_segment_outcome (tenant_id, thread_token, observed_at);

        -- One point per (message, segment ordinal, element, scorer). A score is only comparable
        -- with its scorer and versions, so they are columns rather than an implicit deployment fact.
        -- No text and no embedding vector is stored here.
        CREATE TABLE IF NOT EXISTS proxy_element_point (
            tenant_id           TEXT NOT NULL,
            message_token       TEXT NOT NULL,
            direction           TEXT NOT NULL,
            ordinal             INTEGER NOT NULL,
            element             TEXT NOT NULL,
            scorer              TEXT NOT NULL,
            thread_token        TEXT NOT NULL,
            account_id          TEXT NOT NULL,
            observed_at         TEXT NOT NULL,
            score               REAL NULL,
            nearest_exemplar_id TEXT NULL,
            availability        TEXT NOT NULL,
            availability_reason TEXT NOT NULL,
            model_tag           TEXT NULL,
            prefix_version      TEXT NULL,
            dimension           INTEGER NULL,
            vocabulary_version  TEXT NULL,
            scorer_version      TEXT NULL,
            PRIMARY KEY (tenant_id, message_token, direction, ordinal, element, scorer)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_element_point_thread
            ON proxy_element_point (tenant_id, thread_token, observed_at, ordinal);

        -- Tone is a sibling series with its own model tag and vocabulary. No writer exists in this
        -- slice (nimble's tone service is not published), so no route may invent scores.
        CREATE TABLE IF NOT EXISTS proxy_tone_point (
            tenant_id              TEXT NOT NULL,
            message_token          TEXT NOT NULL,
            direction              TEXT NOT NULL,
            ordinal                INTEGER NOT NULL,
            thread_token           TEXT NOT NULL,
            account_id             TEXT NOT NULL,
            observed_at            TEXT NOT NULL,
            tone_model_tag         TEXT NOT NULL,
            tone_vocabulary_version TEXT NOT NULL,
            availability           TEXT NOT NULL,
            availability_reason    TEXT NOT NULL,
            top_labels             TEXT NOT NULL,
            PRIMARY KEY (tenant_id, message_token, direction, ordinal)
        );

        -- Vectors are off by default: this table exists for a later retention policy and no route
        -- reads it. The Host does not populate it in this slice.
        CREATE TABLE IF NOT EXISTS proxy_element_vector (
            tenant_id      TEXT NOT NULL,
            message_token  TEXT NOT NULL,
            ordinal        INTEGER NOT NULL,
            model_tag      TEXT NOT NULL,
            dimension      INTEGER NOT NULL,
            vector         BLOB NOT NULL,
            PRIMARY KEY (tenant_id, message_token, ordinal, model_tag)
        );

        -- Image observation. One account-scoped content observation per observation_key; the
        -- association token is a parent link, never the unique key. At most two admitted parts and
        -- a realised Core evidence row count of at most 13. No bytes, filename, subject or address.
        CREATE TABLE IF NOT EXISTS proxy_image_outcome (
            tenant_id                  TEXT NOT NULL,
            account_id                 TEXT NOT NULL,
            message_token              TEXT NOT NULL,
            direction                  TEXT NOT NULL,
            thread_token               TEXT NOT NULL,
            observation_key            TEXT NOT NULL,
            content_fingerprint        TEXT NOT NULL,
            accepted_epoch             INTEGER NOT NULL,
            accepted_sequence          INTEGER NOT NULL,
            result_digest              TEXT NOT NULL,
            observed_at                TEXT NOT NULL,
            availability               TEXT NOT NULL,
            availability_reason        TEXT NOT NULL,
            source_version             TEXT NOT NULL,
            image_parts                INTEGER NOT NULL,
            images_skipped_as_chrome   INTEGER NOT NULL,
            images_admitted            INTEGER NOT NULL,
            images_produced            INTEGER NOT NULL,
            images_not_produced        INTEGER NOT NULL,
            images_over_budget         INTEGER NOT NULL,
            first_over_budget_ordinal  INTEGER NULL,
            last_over_budget_ordinal   INTEGER NULL,
            evidence_row_count         INTEGER NOT NULL,
            usage_bytes                INTEGER NOT NULL,
            expires_at                 TEXT NOT NULL,
            retention_days             INTEGER NOT NULL,
            PRIMARY KEY (tenant_id, account_id, direction, observation_key)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_image_outcome_thread
            ON proxy_image_outcome (tenant_id, thread_token, observed_at);
        CREATE INDEX IF NOT EXISTS ix_proxy_image_outcome_child
            ON proxy_image_outcome (tenant_id, account_id, message_token, direction, thread_token);
        CREATE INDEX IF NOT EXISTS ix_proxy_image_outcome_expiry
            ON proxy_image_outcome (expires_at);

        CREATE TABLE IF NOT EXISTS proxy_image_part (
            tenant_id                  TEXT NOT NULL,
            account_id                 TEXT NOT NULL,
            message_token              TEXT NOT NULL,
            direction                  TEXT NOT NULL,
            thread_token               TEXT NOT NULL,
            observation_key            TEXT NOT NULL,
            ordinal                    INTEGER NOT NULL,
            observed_at                TEXT NOT NULL,
            disposition                TEXT NOT NULL,
            availability               TEXT NOT NULL,
            availability_reason        TEXT NOT NULL,
            format                     TEXT NOT NULL,
            declared_format            TEXT NULL,
            width                      INTEGER NULL,
            height                     INTEGER NULL,
            pixels                     INTEGER NULL,
            image_end_offset           INTEGER NULL,
            trailing_bytes             INTEGER NULL,
            observed_trailing_bytes    INTEGER NULL,
            trailing_scan_complete     INTEGER NULL,
            transparency_declared      INTEGER NULL,
            source_version             TEXT NOT NULL,
            findings                   TEXT NOT NULL,
            PRIMARY KEY (tenant_id, account_id, direction, observation_key, ordinal)
        );

        CREATE INDEX IF NOT EXISTS ix_proxy_image_part_thread
            ON proxy_image_part (tenant_id, thread_token, observed_at, ordinal);

        CREATE TABLE IF NOT EXISTS proxy_image_tenant_usage (
            tenant_id  TEXT NOT NULL PRIMARY KEY,
            outcomes   INTEGER NOT NULL,
            used_bytes INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS proxy_image_sweep_state (
            id             INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            last_tenant_id TEXT NULL,
            updated_at     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS proxy_image_epoch (
            id         INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            epoch      INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS host_schema_migration (
            name       TEXT NOT NULL PRIMARY KEY,
            applied_at TEXT NOT NULL
        );

        INSERT OR IGNORE INTO host_schema_migration (name, applied_at)
            VALUES ('proxy-store-v1', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

        -- Seed the epoch/marker only when the image migration marker is absent AND no image
        -- outcome history exists. If history exists without the marker, this is a repair fault:
        -- nothing is reseeded and InitializeEpochAsync fails closed.
        INSERT OR IGNORE INTO proxy_image_epoch (id, epoch, updated_at)
            SELECT 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE NOT EXISTS (
                SELECT 1 FROM host_schema_migration WHERE name = 'proxy-image-v1'
            )
            AND NOT EXISTS (SELECT 1 FROM proxy_image_outcome LIMIT 1);

        INSERT OR IGNORE INTO host_schema_migration (name, applied_at)
            SELECT 'proxy-image-v1', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE NOT EXISTS (SELECT 1 FROM proxy_image_outcome LIMIT 1);

        -- Minted API keys. A row holds a per-key salt and a slow-KDF digest and NEVER the key, so
        -- copying this file does not hand over a usable credential and does not hand over a fast
        -- digest to attack offline either. Named host_* for the same reason as the ledger above.
        --
        -- key_id is the public handle the key carries in its own value. It is what makes lookup a
        -- single indexed read and keeps the expensive derivation to one per authentication, without
        -- writing down anything derived cheaply from the secret: which is the property that would
        -- make the store crackable.
        --
        -- Revocation stamps rather than deletes: who killed a credential and when is asked
        -- afterwards. A revoked row also goes on claiming its principal_id, so revoking a mint
        -- cannot silently restore an environment entry of the same name.
        CREATE TABLE IF NOT EXISTS host_principal (
            key_id                     TEXT NOT NULL PRIMARY KEY,
            principal_id               TEXT NOT NULL,
            tenant_id                  TEXT NOT NULL,
            kdf_algorithm              TEXT NOT NULL,
            kdf_iterations             INTEGER NOT NULL,
            salt                       BLOB NOT NULL,
            digest                     BLOB NOT NULL,
            privileges                 TEXT NOT NULL,
            approved_sender_identities TEXT NOT NULL,
            created_at                 TEXT NOT NULL,
            created_by                 TEXT NOT NULL,
            revoked_at                 TEXT NULL,
            revoked_by                 TEXT NULL
        );

        -- One live key per principal. Partial rather than total, because a revoked row stays and a
        -- principal may legitimately be minted again afterwards; what must not exist is two keys
        -- that both authenticate as one identity with nothing saying which is current.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_host_principal_live
            ON host_principal (principal_id) WHERE revoked_at IS NULL;

        -- The store's change counter. Every mint and every revocation increments it in the same
        -- transaction as the change, and a resolution cache validates against it rather than against
        -- a local eviction it cannot perform: the CLI that revokes a key is a different process.
        CREATE TABLE IF NOT EXISTS host_principal_store (
            id      INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL
        );

        INSERT OR IGNORE INTO host_principal_store (id, version) VALUES (1, 0);

        -- Quarantine releases are audited. A release is a decision someone made, and the record
        -- of who made it is not optional.
        CREATE TABLE IF NOT EXISTS quarantine_release (
            tenant_id     TEXT NOT NULL,
            queue_id      TEXT NOT NULL,
            recipient_key TEXT NOT NULL,
            released_at   TEXT NOT NULL,
            released_by   TEXT NOT NULL,
            PRIMARY KEY (tenant_id, queue_id, recipient_key)
        );

        -- Verified chat events waiting to be assessed, written before the platform is answered.
        --
        -- This is an intake rather than a delivery queue, and the distinction is the whole reason it
        -- exists. Chat has no delivery responsibility, so the queue's role does not transfer; but the
        -- answer we give the platform is this path's equivalent of the mail path's 250, and answering
        -- it and then losing the event on a crash would be accepting a responsibility we cannot
        -- honour. Persisting first is what makes the answer true.
        --
        -- The primary key is the platform's own event id, so a redelivery of something already
        -- waiting is refused by the database rather than by a check that could race it.
        -- Null until the event has been assessed, so the row is both the durable hand-off and the
        -- record that an event was already dealt with. Removing the row instead would let a platform
        -- retry, which arrives after the answer and inside its retry window, be assessed a second
        -- time: that would double every observation the behavioural engine counts, which is a rate
        -- change it cannot tell from real traffic.
        CREATE TABLE IF NOT EXISTS host_chat_intake (
            event_id    TEXT NOT NULL,
            received_at TEXT NOT NULL,
            payload     TEXT NOT NULL,
            assessed_at TEXT NULL,
            PRIMARY KEY (event_id)
        );

        -- The drain takes the oldest unanswered first, so a burst cannot starve what arrived before
        -- it. Partial on assessed_at so the index covers only the rows the drain actually reads.
        CREATE INDEX IF NOT EXISTS ix_host_chat_intake_waiting
            ON host_chat_intake (received_at)
            WHERE assessed_at IS NULL;

        -- Answered rows are pruned by age. Bounded retention rather than growth, and the window has
        -- to exceed the platform's retry window or a late retry is assessed twice after all.
        CREATE INDEX IF NOT EXISTS ix_host_chat_intake_assessed
            ON host_chat_intake (assessed_at);

        -- The emergency stop, as a list of transitions rather than a mutable flag.
        --
        -- Persisted because a stop that silently disengages when the host restarts is worse than one
        -- that was never wired: it teaches an operator to trust it, and a restart is exactly when
        -- nobody is looking. Append-only because "is it engaged" is the easy question and "who pulled
        -- it, and when" is the one asked afterwards.
        --
        -- State is the most recent row. An integer primary key is the rowid, so that read is an
        -- ordered lookup rather than a scan and needs no index of its own.
        CREATE TABLE IF NOT EXISTS kill_switch_event (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            engaged     INTEGER NOT NULL,
            actor       TEXT NOT NULL,
            occurred_at TEXT NOT NULL
        );
        """;
}

/// <summary>
/// Raised when durable storage cannot be read or written.
/// </summary>
/// <remarks>
/// Callers must translate this into a temporary failure. It must never be swallowed into a
/// successful acceptance, accepting a message we could not persist is precisely how mail is lost.
/// </remarks>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
