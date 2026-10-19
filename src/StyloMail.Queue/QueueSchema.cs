using Microsoft.Data.Sqlite;

namespace StyloMail.Queue;

/// <summary>
/// Schema for the durable delivery queue.
/// </summary>
/// <remarks>
/// Lives here rather than in the Persistence project because the queue owns its own durability
/// contract: metadata rows and spooled payloads must commit in a defined order, and that ordering
/// is a property of this subsystem.
///
/// <para>
/// Tenant is on every row, and the queue is bounded per tenant, so one tenant cannot exhaust
/// the spool and deny service to the others.
/// </para>
/// </remarks>
public static class QueueSchema
{
    /// <summary>Current schema version. Bump when the DDL below changes.</summary>
    public const int CurrentVersion = 7;

    /// <summary>Creates the schema if absent, stamping the version.</summary>
    /// <remarks>
    /// <para>
    /// <c>journal_mode = WAL</c> is set before the transaction opens: it is a persistent property of
    /// the database file and cannot be changed inside a transaction.
    /// </para>
    /// <para>
    /// <b>Version mismatch throws rather than proceeding.</b> Every table below is created with
    /// <c>IF NOT EXISTS</c>, so a database written by an older version of this code keeps its old
    /// columns and fails later with a confusing "no such column" deep inside an accept path. Failing
    /// loudly at startup, when the operation is still safe to refuse, is the kinder behaviour for
    /// a component whose job is not to lose mail.
    /// </para>
    /// </remarks>
    public static int EnsureCreated(SqliteConnection connection, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(timeProvider);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS queue_schema_version (
                version    INTEGER NOT NULL,
                applied_at TEXT    NOT NULL
            );
            """);

        // Before the DDL, not after. The DDL includes indexes over columns like `state`, so a
        // colliding table of the wrong shape blows up inside CREATE INDEX with a bare
        // "no such column" long before a post-hoc check could explain what actually happened.
        VerifyShape(connection, transaction, requireAllTables: false);

        Execute(connection, transaction, ItemDdl);
        Execute(connection, transaction, RecipientDdl);
        Execute(connection, transaction, AttemptDdl);
        Execute(connection, transaction, ReviewEventDdl);
        Execute(connection, transaction, ReviewOpeningDdl);
        EnsureLegacyDigestColumn(connection, transaction);

        var version = ReadVersion(connection, transaction);
        if (version == 0)
        {
            using var stamp = connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText =
                "INSERT INTO queue_schema_version (version, applied_at) VALUES ($v, $at);";
            stamp.Parameters.AddWithValue("$v", CurrentVersion);
            stamp.Parameters.AddWithValue("$at", timeProvider.GetUtcNow().ToString("O"));
            stamp.ExecuteNonQuery();
            version = CurrentVersion;
        }
        else if (version is 4 or 5 or 6)
        {
            // Additive migration. v4 gains the review deadline and the opening chain; v5 already
            // has the deadline and gains the opening chain. Existing rows keep NULL, which is the
            // honest "this row predates the review state" value. No row is rewritten and no
            // accepted message changes.
            if (version == 4)
            {
                ApplyReviewDeadlineMigration(connection, transaction);
            }

            ApplyReviewOpeningMigration(connection, transaction);

            using var stamp = connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText =
                "INSERT INTO queue_schema_version (version, applied_at) VALUES ($v, $at);";
            stamp.Parameters.AddWithValue("$v", CurrentVersion);
            stamp.Parameters.AddWithValue("$at", timeProvider.GetUtcNow().ToString("O"));
            stamp.ExecuteNonQuery();
            version = CurrentVersion;
        }
        else if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The delivery queue database is at schema version {version} but this build expects " +
                $"{CurrentVersion}. Refusing to open it: the tables are created with IF NOT EXISTS, so " +
                "proceeding would leave rows with missing columns and fail later while accepting mail. " +
                "Migrate or re-create the queue tables before starting.");
        }

        Execute(connection, transaction,
            """
            CREATE INDEX IF NOT EXISTS ix_queue_recipient_review
                ON queue_recipient (state, review_deadline);
            """);

        VerifyShape(connection, transaction, requireAllTables: true);

        transaction.Commit();
        return version;
    }

    /// <summary>
    /// Columns that must exist for the schema to be the one this build understands.
    /// </summary>
    /// <remarks>
    /// A representative column per table per schema revision, not every column, the point is to
    /// notice that the table is <em>someone else's</em>, and the newest columns are the ones a
    /// colliding or stale definition will lack.
    /// </remarks>
    private static readonly (string Table, string[] Columns)[] RequiredShape =
    [
        ("queue_item", ["queue_id", "untrusted_message_id", "payload_bytes", "idempotency_key", "purged_at"]),
        ("queue_recipient", ["queue_id", "recipient_key", "next_attempt_at", "re_evaluate_by", "hold_surfaced_at"]),
        ("queue_attempt", ["queue_id", "recipient_key", "outcome", "attempted_at"]),
        ("queue_review_event", ["event_id", "queue_id", "assessment_id", "policy_reference", "at"]),
        ("queue_review_opening", ["tenant_id", "queue_id", "opening_event_id", "assessment_id", "pack_id"]),
    ];

    /// <summary>
    /// Confirms the tables really have the shape this build expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because <c>CREATE TABLE IF NOT EXISTS</c> silently does nothing when a table
    /// of that name already exists with a different shape.</b> The version guard above does not
    /// catch it: the version table is ours, so on first run it is simply empty and gets stamped,
    /// while <c>queue_item</c> is whatever someone else created. Every test would pass against a
    /// scratch database and the failure would land on the first real accept as a confusing
    /// "no such column", with mail on the line.
    /// </para>
    /// <para>
    /// The <c>queue_</c> prefix exists to make this collision unlikely; this check exists because
    /// "unlikely" is not "impossible", and because a shared SQLite file is exactly where it happens.
    /// </para>
    /// </remarks>
    /// <param name="requireAllTables">
    /// True once the DDL has run, when every table must exist. False beforehand, when an absent
    /// table simply means a fresh database and only a present-but-wrong one is a problem.
    /// </param>
    private static void VerifyShape(
        SqliteConnection connection, SqliteTransaction transaction, bool requireAllTables)
    {
        foreach (var (table, required) in RequiredShape)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"PRAGMA table_info({table});";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    present.Add(reader.GetString(1));
                }
            }

            if (present.Count == 0)
            {
                if (!requireAllTables)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Table '{table}' is missing after the queue schema was created. The database is " +
                    "not the one this build expects.");
            }

            var missing = required.Where(c => !present.Contains(c)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Table '{table}' exists but is missing column(s) {string.Join(", ", missing)}. This " +
                    "is the signature of a name collision: CREATE TABLE IF NOT EXISTS silently accepts a " +
                    "table of the same name with a different shape, so these rows are NOT the queue's. " +
                    "Rename one of the tables, do not drop anything until you know whose rows these are.");
            }
        }
    }

    /// <summary>
    /// Adds the review deadline column to a v4 queue. Guarded, so a partially migrated database is
    /// not altered twice.
    /// </summary>
    private static void ApplyReviewDeadlineMigration(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA table_info(queue_recipient);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                present.Add(reader.GetString(1));
            }
        }

        if (!present.Contains("review_deadline"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE queue_recipient ADD COLUMN review_deadline TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds the named legacy-digest flag to a tagged opening table that predates it. This is a
    /// readback marker, so a migrated row can say "the digest was absent" rather than "unread".
    /// </summary>
    private static void EnsureLegacyDigestColumn(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA table_info(queue_review_opening);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count == 0
            || !columns.Contains("opening_source")
            || columns.Contains("legacy_digest_absent"))
        {
            return;
        }

        Execute(connection, transaction,
            "ALTER TABLE queue_review_opening ADD COLUMN legacy_digest_absent INTEGER NOT NULL DEFAULT 0;");
        Execute(connection, transaction,
            """
            UPDATE queue_review_opening
               SET legacy_digest_absent = 1
             WHERE opening_source = 'pack' AND evidence_digest IS NULL;
            """);
    }

    /// <summary>
    /// Adds the opening-event column and the tagged source to an existing queue, recreating the
    /// opening table once so fallback provenance can carry nulls.
    /// </summary>
    private static void ApplyReviewOpeningMigration(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var eventColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA table_info(queue_review_event);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                eventColumns.Add(reader.GetString(1));
            }
        }

        if (!eventColumns.Contains("opening_event_id"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE queue_review_event ADD COLUMN opening_event_id TEXT NULL;";
            alter.ExecuteNonQuery();
        }

        var openingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA table_info(queue_review_opening);";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                openingColumns.Add(reader.GetString(1));
            }
        }

        if (openingColumns.Count == 0)
        {
            Execute(connection, transaction, ReviewOpeningDdl);
            return;
        }

        if (!openingColumns.Contains("opening_source"))
        {
            // SQLite cannot drop NOT NULL in place, so recreate the table and map every existing
            // opening to pack. No row is reclassified and no missing value is invented.
            Execute(connection, transaction, "ALTER TABLE queue_review_opening RENAME TO queue_review_opening_old;");
            Execute(connection, transaction, ReviewOpeningDdl);
            Execute(connection, transaction,
                """
                INSERT INTO queue_review_opening
                    (tenant_id, queue_id, opening_event_id, assessment_id, opening_source,
                     pack_id, pack_version, rule_set_id, rule_set_version, rule_id, rule_version,
                     reason_code, reason, evidence_digest, legacy_digest_absent,
                     unavailable_reason, fallback_deadline, decided_at)
                SELECT tenant_id, queue_id, opening_event_id, assessment_id, 'pack',
                       pack_id, pack_version, rule_set_id, rule_set_version, rule_id, rule_version,
                       reason_code, reason, evidence_digest,
                       CASE WHEN evidence_digest IS NULL THEN 1 ELSE 0 END,
                       NULL, NULL, decided_at
                  FROM queue_review_opening_old;
                """);
            Execute(connection, transaction, "DROP TABLE queue_review_opening_old;");
        }
    }

    private static int ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT version FROM queue_schema_version ORDER BY rowid DESC LIMIT 1;";
        return cmd.ExecuteScalar() is long value ? (int)value : 0;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private const string ItemDdl =
        """
        CREATE TABLE IF NOT EXISTS queue_item (
            queue_id            TEXT    NOT NULL PRIMARY KEY,
            tenant_id           TEXT    NOT NULL,
            internal_message_id TEXT    NOT NULL,
            payload_reference   TEXT    NOT NULL,
            mime_digest         TEXT    NOT NULL,
            direction           INTEGER NOT NULL,
            mail_from           TEXT    NOT NULL,

            -- Identity comes from the authenticated principal that handed us the message, never
            -- from a header. Recorded so the envelope can be reconstructed without a second lookup.
            trusted_principal_id TEXT   NOT NULL,

            -- The message's own Message-ID header. UNTRUSTED: sender-supplied, recorded for
            -- diagnostics and loop tracing only. Never a key and never a deduplication guarantee.
            untrusted_message_id TEXT   NULL,

            -- Recorded at acceptance rather than measured from the filesystem later, so a
            -- per-tenant byte budget is one indexed SUM instead of a directory walk, and so the
            -- accounting cannot drift when a file is swept or purged.
            payload_bytes       INTEGER NOT NULL,

            -- Tenant-scoped client idempotency key for HTTP submission. NULL means "not a
            -- re-playable submission"; the unique index below is partial so those rows do not
            -- collide with each other on NULL.
            idempotency_key     TEXT    NULL,

            state               INTEGER NOT NULL,
            attempts            INTEGER NOT NULL DEFAULT 0,
            -- NULL means the hop count was never observed, which is a different fact from
            -- zero prior hops. Collapsing them at rest would make the loop backstop read as
            -- enforced while never firing.
            hop_count           INTEGER NULL,

            next_attempt_at     TEXT    NULL,
            expires_at          TEXT    NULL,

            -- Lease-based claiming. A lease that outlives its worker is reclaimed by the
            -- recovery sweep, so a crash mid-delivery does not strand a message forever.
            lease_owner         TEXT    NULL,
            lease_expires_at    TEXT    NULL,

            -- Set when retention releases the payload bytes. The metadata row deliberately
            -- survives: it is the audit record of a message we accepted, and deleting it would
            -- erase the only trace that we ever held it.
            purged_at           TEXT    NULL,

            last_error          TEXT    NULL,
            created_at          TEXT    NOT NULL,
            updated_at          TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_queue_item_claimable
            ON queue_item (state, next_attempt_at);
        CREATE INDEX IF NOT EXISTS ix_queue_item_tenant
            ON queue_item (tenant_id, state);
        CREATE INDEX IF NOT EXISTS ix_queue_item_lease
            ON queue_item (state, lease_expires_at);
        CREATE INDEX IF NOT EXISTS ix_queue_item_expiry
            ON queue_item (state, expires_at);
        CREATE INDEX IF NOT EXISTS ix_queue_item_spool
            ON queue_item (tenant_id, purged_at);

        -- The flow summary reads "messages accepted in [from, to) grouped by direction and state".
        -- Without this the planner can only narrow to the tenant and then filter every message the
        -- tenant has ever accepted, which is the tenant's whole history rather than the window. The
        -- extra two columns make it covering, so the aggregate never touches the row payload.
        CREATE INDEX IF NOT EXISTS ix_queue_item_tenant_created
            ON queue_item (tenant_id, created_at, direction, state);

        -- The same aggregate reports how many decisions have no accepted queue row, which is the
        -- honest gap for direction. That is a LEFT JOIN from the ledger on (tenant, message id);
        -- without this index SQLite builds a transient index or scans the queue per ledger row.
        CREATE INDEX IF NOT EXISTS ix_queue_item_tenant_message
            ON queue_item (tenant_id, internal_message_id);

        -- The entity directory recovers a masked address from recent envelopes. That lookup asks
        -- for one direction ordered by time, and the window index above puts created_at before
        -- direction, so the direction is a residual predicate: a tenant with a million outbound
        -- messages and few inbound would walk the whole outbound history to fill a page of inbound
        -- correspondents. Direction-first makes both candidate scans a two-column seek.
        CREATE INDEX IF NOT EXISTS ix_queue_item_tenant_direction_created
            ON queue_item (tenant_id, direction, created_at);

        -- One submission per tenant per idempotency key. Partial, because most SMTP submissions
        -- carry no key and NULLs must not collide. This index is what makes a concurrent double
        -- submission safe: the loser of the race is rejected by the database, not by a
        -- check-then-insert that a second writer can slip through.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_queue_item_idempotency
            ON queue_item (tenant_id, idempotency_key)
            WHERE idempotency_key IS NOT NULL;
        """;

    private const string RecipientDdl =
        """
        CREATE TABLE IF NOT EXISTS queue_recipient (
            queue_id        TEXT    NOT NULL,
            recipient_key   TEXT    NOT NULL,
            state           INTEGER NOT NULL,
            attempts        INTEGER NOT NULL DEFAULT 0,
            last_attempt_at TEXT    NULL,
            delivered_at    TEXT    NULL,

            -- Backoff is per recipient: this recipient's own next due time, independent of the
            -- others on the same message.
            next_attempt_at TEXT    NULL,

            re_evaluate_by  TEXT    NULL,

            -- Deadline for an open Review. NULL on every row that predates the review state and
            -- on every non-review row. Required when state = Review (7); the store refuses a
            -- review admission without it rather than inventing an indefinite one.
            review_deadline TEXT    NULL,

            -- When a lapsed hold was last reported to policy. Keeps the sweep idempotent without
            -- destroying the deadline, so "which holds are still awaiting a decision" stays
            -- answerable after the fact.
            hold_surfaced_at TEXT   NULL,

            last_error      TEXT    NULL,
            PRIMARY KEY (queue_id, recipient_key),
            FOREIGN KEY (queue_id) REFERENCES queue_item (queue_id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_queue_recipient_pending
            ON queue_recipient (queue_id, state);

        -- Drives the expired-hold sweep: holds whose re-evaluation deadline has passed and which
        -- therefore need a policy decision.
        CREATE INDEX IF NOT EXISTS ix_queue_recipient_hold
            ON queue_recipient (state, re_evaluate_by);
        """;

    private const string ReviewEventDdl =
        """
        -- Every review transition, written in the same transaction as the recipient state change.
        -- The queue owns this table because the audit must commit with the state it describes; a
        -- Host-side table would need a two-phase write. policy_reference is opaque to the queue and
        -- carries the pack/rule versions from the decision that opened the review.
        CREATE TABLE IF NOT EXISTS queue_review_event (
            event_id        TEXT NOT NULL PRIMARY KEY,
            tenant_id       TEXT NOT NULL,
            queue_id        TEXT NOT NULL,
            recipient_key   TEXT NOT NULL,
            assessment_id   TEXT NULL,
            event           TEXT NOT NULL,
            from_state      INTEGER NOT NULL,
            to_state        INTEGER NOT NULL,
            actor           TEXT NOT NULL,
            reason          TEXT NOT NULL,
            policy_reference TEXT NULL,
            opening_event_id TEXT NULL,
            at              TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_queue_review_event_history
            ON queue_review_event (tenant_id, queue_id, at DESC);
        """;

    private const string ReviewOpeningDdl =
        """
        -- The immutable decision that opened a review. A pack opening names the exact assessment,
        -- pack, rule set and deciding rule; a fallback opening has no pack provenance and names the
        -- unavailable reason and bounded deadline instead. The CHECK prevents a mixed row.
        CREATE TABLE IF NOT EXISTS queue_review_opening (
            tenant_id        TEXT NOT NULL,
            queue_id         TEXT NOT NULL,
            opening_event_id TEXT NOT NULL,
            assessment_id    TEXT NOT NULL,
            opening_source   TEXT NOT NULL CHECK(opening_source IN ('pack','fallback')),
            pack_id          TEXT NULL,
            pack_version     TEXT NULL,
            rule_set_id      TEXT NULL,
            rule_set_version TEXT NULL,
            rule_id          TEXT NULL,
            rule_version     INTEGER NULL,
            reason_code      TEXT NOT NULL,
            reason           TEXT NOT NULL,
            evidence_digest  TEXT NULL,
            legacy_digest_absent INTEGER NOT NULL DEFAULT 0,
            unavailable_reason TEXT NULL,
            fallback_deadline  TEXT NULL,
            decided_at       TEXT NOT NULL,
            PRIMARY KEY (tenant_id, queue_id),
            CHECK (
                (opening_source = 'pack'
                    AND pack_id IS NOT NULL AND pack_version IS NOT NULL
                    AND rule_set_id IS NOT NULL AND rule_set_version IS NOT NULL
                    AND unavailable_reason IS NULL AND fallback_deadline IS NULL
                    AND (
                        (evidence_digest IS NOT NULL AND legacy_digest_absent = 0)
                        OR
                        (evidence_digest IS NULL AND legacy_digest_absent = 1)
                    ))
                OR
                (opening_source = 'fallback'
                    AND pack_id IS NULL AND pack_version IS NULL
                    AND rule_set_id IS NULL AND rule_set_version IS NULL
                    AND rule_id IS NULL AND rule_version IS NULL
                    AND evidence_digest IS NULL
                    AND legacy_digest_absent = 0
                    AND unavailable_reason IS NOT NULL AND fallback_deadline IS NOT NULL)
            )
        );

        CREATE INDEX IF NOT EXISTS ix_queue_review_opening_queue
            ON queue_review_opening (tenant_id, queue_id);
        """;

    private const string AttemptDdl =
        """
        -- Delivery attempts are append-only history. Retries must not overwrite the record of
        -- what was already tried. Nothing in this assembly ever updates or deletes a row here.
        CREATE TABLE IF NOT EXISTS queue_attempt (
            attempt_id    INTEGER PRIMARY KEY AUTOINCREMENT,
            queue_id      TEXT    NOT NULL,
            recipient_key TEXT    NOT NULL,
            worker_id     TEXT    NOT NULL,
            outcome       TEXT    NOT NULL,
            detail        TEXT    NULL,
            attempted_at  TEXT    NOT NULL,
            FOREIGN KEY (queue_id) REFERENCES queue_item (queue_id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_queue_attempt_queue
            ON queue_attempt (queue_id, attempted_at);
        """;
}
