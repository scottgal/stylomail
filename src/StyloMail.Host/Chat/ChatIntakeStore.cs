using Microsoft.Data.Sqlite;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Chat;

/// <summary>One verified platform event, and whether it has been dealt with.</summary>
public sealed record ChatIntakeEntry(string EventId, string Payload, DateTimeOffset ReceivedAt);

/// <summary>What happened when a verified event was offered to the durable intake.</summary>
public enum ChatIntakeAdmission
{
    /// <summary>Stored, and not seen before. The caller may answer the platform.</summary>
    Admitted = 0,

    /// <summary>This event id is already stored, waiting or answered. The caller answers but does not re-assess.</summary>
    AlreadyKnown = 1,

    /// <summary>The intake is at its bound. The caller must refuse so the platform retries.</summary>
    Full = 2,
}

/// <summary>
/// The durable hand-off between answering the platform and assessing the event.
/// </summary>
/// <remarks>
/// <para>
/// <b>An intake, not a delivery queue.</b> Chat has no delivery responsibility, so the queue's role
/// does not transfer. What this holds is work: a verified event between the HTTP answer and the
/// assessment. But the answer given to the platform is this path's equivalent of the mail path's
/// <c>250</c>, and <b>answering it and then losing the event on a crash would be accepting a
/// responsibility we cannot honour</b>, so the row is written before the answer and the answer is
/// only true because of it.
/// </para>
/// <para>
/// <b>The bound is enforced by refusing, never by dropping.</b> A refused event is one the platform
/// retries, which is honest backpressure; a dropped one is traffic we acknowledged and never looked
/// at.
/// </para>
/// </remarks>
public interface IChatIntakeStore
{
    /// <summary>
    /// Stores a verified event, or says why it was not stored.
    /// </summary>
    /// <remarks>
    /// Called before the platform is answered. Anything other than
    /// <see cref="ChatIntakeAdmission.Admitted"/> and <see cref="ChatIntakeAdmission.AlreadyKnown"/>
    /// must become a refusal, because the answer would otherwise be a claim the store does not
    /// support.
    /// </remarks>
    ChatIntakeAdmission Admit(ChatIntakeEntry entry, int capacity);

    /// <summary>The oldest events that have not been assessed, bounded.</summary>
    IReadOnlyList<ChatIntakeEntry> Waiting(int max);

    /// <summary>Marks an event as dealt with, whether it was assessed or given up on.</summary>
    void Complete(string eventId, DateTimeOffset at);

    /// <summary>
    /// Removes answered events older than the retention window.
    /// </summary>
    /// <remarks>
    /// The window must exceed the platform's retry window, or a late retry finds no row and is
    /// assessed twice after all, which is the thing the answered row is kept to prevent.
    /// </remarks>
    int Prune(DateTimeOffset olderThan);
}

/// <summary>SQLite-backed chat intake, in the host's own database.</summary>
public sealed class SqliteChatIntakeStore : IChatIntakeStore
{
    private readonly HostDatabase _database;

    public SqliteChatIntakeStore(HostDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public ChatIntakeAdmission Admit(ChatIntakeEntry entry, int capacity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            // Both operations in one transaction, because a count taken outside it could be stale by
            // the time the insert runs and the bound would then be exceeded by however many writers
            // raced it.
            long waiting;

            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM host_chat_intake WHERE assessed_at IS NULL;";
                waiting = (long)(count.ExecuteScalar() ?? 0L);
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;

                // The primary key is the platform's own event id, so a redelivery is refused by the
                // database rather than by a check that another writer could race. INSERT OR IGNORE
                // reports it through the row count instead of as a constraint failure, which keeps
                // "already known" distinguishable from a storage fault.
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO host_chat_intake (event_id, received_at, payload)
                    VALUES ($id, $received, $payload);
                    """;
                insert.Parameters.AddWithValue("$id", entry.EventId);
                insert.Parameters.AddWithValue("$received", entry.ReceivedAt.ToString("O"));
                insert.Parameters.AddWithValue("$payload", entry.Payload);

                if (insert.ExecuteNonQuery() == 0)
                {
                    transaction.Commit();
                    return ChatIntakeAdmission.AlreadyKnown;
                }
            }

            if (waiting >= capacity)
            {
                // Rolled back rather than kept: an event stored past the bound would be one the
                // caller then refused, and a refusal the platform retries would find the row and be
                // treated as already known, so the event would sit unassessed with nobody coming
                // back for it.
                transaction.Rollback();
                return ChatIntakeAdmission.Full;
            }

            transaction.Commit();
            return ChatIntakeAdmission.Admitted;
        }
        catch (SqliteException ex)
        {
            // Never swallowed into an admission. Answering the platform on the strength of a write
            // that did not happen is accepting responsibility we cannot honour.
            throw new StorageUnavailableException("A chat event could not be stored for assessment.", ex);
        }
    }

    public IReadOnlyList<ChatIntakeEntry> Waiting(int max)
    {
        var entries = new List<ChatIntakeEntry>();

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT event_id, payload, received_at
                  FROM host_chat_intake
                 WHERE assessed_at IS NULL
                 ORDER BY received_at ASC, event_id ASC
                 LIMIT $max;
                """;
            command.Parameters.AddWithValue("$max", max);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new ChatIntakeEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(
                        reader.GetString(2),
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("Waiting chat events could not be read.", ex);
        }

        return entries;
    }

    public void Complete(string eventId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // Marks rather than deletes, so a retry arriving after the assessment is recognised as
            // one that has been dealt with. Deleting here is what would let it be assessed twice.
            command.CommandText =
                "UPDATE host_chat_intake SET assessed_at = $at WHERE event_id = $id;";
            command.Parameters.AddWithValue("$at", at.ToString("O"));
            command.Parameters.AddWithValue("$id", eventId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("A chat event could not be marked as assessed.", ex);
        }
    }

    public int Prune(DateTimeOffset olderThan)
    {
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            command.CommandText =
                "DELETE FROM host_chat_intake WHERE assessed_at IS NOT NULL AND assessed_at < $before;";
            command.Parameters.AddWithValue("$before", olderThan.ToString("O"));
            return command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("Answered chat events could not be pruned.", ex);
        }
    }
}
