using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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
    public void EnsureCreated()
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

        ApplyColumnMigrations(connection);
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
    private static void ApplyColumnMigrations(SqliteConnection connection)
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
