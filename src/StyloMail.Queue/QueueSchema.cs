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
    public const int CurrentVersion = 4;

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
        else if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The delivery queue database is at schema version {version} but this build expects " +
                $"{CurrentVersion}. Refusing to open it: the tables are created with IF NOT EXISTS, so " +
                "proceeding would leave rows with missing columns and fail later while accepting mail. " +
                "Migrate or re-create the queue tables before starting.");
        }

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
