using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StyloMail.Core;
using StyloMail.Host.Serialization;
using StyloMail.Host.Storage;
using StyloMail.Host.Traffic;

namespace StyloMail.Host.Decisions;

/// <summary>SQLite-backed decision ledger.</summary>
/// <remarks>
/// The whole assessment is stored as JSON alongside its indexed columns. The columns exist so the
/// ledger can be queried and audited without deserialising anything; the document exists because a
/// decision is a versioned artefact whose full shape matters more than its queryability, and
/// normalising it into tables would create a migration burden every time the assessment contract
/// gains a field.
/// </remarks>
public sealed class SqliteDecisionLedger : IDecisionLedger
{
    private readonly HostDatabase _database;
    private readonly ITrafficEvents _events;

    public SqliteDecisionLedger(HostDatabase database, ITrafficEvents events)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(events);

        _database = database;
        _events = events;
    }

    public Task RecordAsync(MailAssessment assessment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(assessment, HostJson.Options);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO host_decision_ledger
                    (tenant_id, assessment_id, internal_message_id, action, recorded_at, payload)
                VALUES ($tenant, $assessment, $message, $action, $recorded, $payload);
                """;

            command.Parameters.AddWithValue("$tenant", assessment.TenantId);
            command.Parameters.AddWithValue("$assessment", assessment.AssessmentId);
            command.Parameters.AddWithValue("$message", assessment.InternalMessageId);
            command.Parameters.AddWithValue("$action", assessment.Action.ToString());
            command.Parameters.AddWithValue("$recorded", assessment.AssessedAt.ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);

            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The decision ledger could not be written.", ex);
        }

        // Announced after the write and outside the catch, because the write is the completion
        // boundary and the announcement is about the record existing rather than about the
        // assessment having been made. A decision that failed to record is not announced: a console
        // told to re-read a row that is not there is being sent to look at nothing.
        //
        // The instant is the assessment's own timestamp, which is the value this ledger stores in
        // recorded_at, so the hint and the row it points at agree about when the decision happened.
        _events.Publish(TrafficEvent.DecisionRecorded(
            assessment.TenantId,
            assessment.AssessmentId,
            assessment.AssessedAt));

        return Task.CompletedTask;
    }

    public Task<MailAssessment?> FindAsync(
        string tenantId,
        string assessmentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessmentId);
        cancellationToken.ThrowIfCancellationRequested();

        string? payload;

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // Tenant is part of the WHERE clause rather than a check performed afterwards, so a
            // row belonging to another tenant is never even materialised.
            command.CommandText =
                "SELECT payload FROM host_decision_ledger WHERE tenant_id = $tenant AND assessment_id = $assessment;";
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$assessment", assessmentId);

            payload = command.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The decision ledger could not be read.", ex);
        }

        // PersistedRead, not Options: this document was written by whatever build was running at the
        // time, so a member added since then has to be tolerated rather than treated as corruption.
        return Task.FromResult(
            payload is null
                ? null
                : JsonSerializer.Deserialize<MailAssessment>(payload, HostJson.PersistedRead));
    }

    public Task<DecisionListingPage> ListAsync(
        DecisionListingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId);
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Math.Clamp(query.Limit, 1, DecisionListingLimits.MaxPageSize);
        var (afterAt, afterId) = DecodeCursor(query.After);

        // Rows are held as pairs rather than in parallel lists, and that is not a style choice: the
        // cursor must name the *last row returned* by both of its components, and two parallel
        // collections are exactly how those two components come to describe different rows. The
        // queue's listing shipped that bug — it paired the probe row's timestamp with the kept row's
        // id — and it dropped entries silently across pages on roughly half of runs.
        var rows = new List<(string AssessmentId, string RecordedAt, string Payload)>(limit + 1);

        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();

            // One extra row tells us whether a further page exists without a second COUNT query.
            command.CommandText =
                """
                SELECT assessment_id, recorded_at, payload
                  FROM host_decision_ledger
                 WHERE tenant_id = $tenant
                   AND ($action IS NULL OR action = $action)
                   AND ($message IS NULL OR internal_message_id = $message)
                   AND ($afterAt IS NULL
                        OR recorded_at < $afterAt
                        OR (recorded_at = $afterAt AND assessment_id < $afterId))
                 ORDER BY recorded_at DESC, assessment_id DESC
                 LIMIT $limit;
                """;

            command.Parameters.AddWithValue("$tenant", query.TenantId);
            command.Parameters.AddWithValue(
                "$action", query.Action is { } action ? action.ToString() : DBNull.Value);

            // The message-id filter is an equality on an indexed column, like the action filter —
            // it narrows the query rather than the page, which is what makes it honest to offer.
            command.Parameters.AddWithValue(
                "$message",
                string.IsNullOrWhiteSpace(query.InternalMessageId) ? DBNull.Value : query.InternalMessageId);
            command.Parameters.AddWithValue("$afterAt", (object?)afterAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$afterId", (object?)afterId ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        catch (SqliteException ex)
        {
            throw new StorageUnavailableException("The decision ledger could not be read.", ex);
        }

        string? nextCursor = null;

        if (rows.Count > limit)
        {
            // Drop the probe row, then take the cursor from what is now the last *kept* row. Both
            // components come from that one row — see the comment on `rows` above.
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(rows[^1].RecordedAt, rows[^1].AssessmentId);
        }

        var items = new List<MailAssessment>(rows.Count);

        foreach (var row in rows)
        {
            if (JsonSerializer.Deserialize<MailAssessment>(row.Payload, HostJson.PersistedRead) is { } assessment)
            {
                items.Add(assessment);
            }
        }

        return Task.FromResult(new DecisionListingPage { Items = items, NextCursor = nextCursor });
    }

    /// <summary>
    /// Encodes the position to resume from.
    /// </summary>
    /// <remarks>
    /// Opaque to the caller rather than a documented format, so the ordering key is ours to change.
    /// It carries no tenant and is not a capability: the query's own tenant always governs what is
    /// returned, so tampering with a cursor can only choose a page, never a tenant's data.
    /// </remarks>
    /// <summary>
    /// Separator between the two halves of a cursor.
    /// </summary>
    /// <remarks>
    /// A newline, because it cannot occur in either half: the timestamp is ISO-8601 and the id is
    /// generated. Any printable choice would work today and stop working the day one of the two
    /// formats changed.
    /// </remarks>
    private const char CursorSeparator = '\n';

    /// <summary>
    /// Encodes the position to resume from.
    /// </summary>
    /// <remarks>
    /// Opaque to the caller rather than a documented format, so the ordering key is ours to change.
    /// It carries no tenant and is not a capability: the query's own tenant always governs what is
    /// returned, so tampering with a cursor can only choose a page, never a tenant's data.
    /// </remarks>
    private static string EncodeCursor(string recordedAt, string assessmentId) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{recordedAt}{CursorSeparator}{assessmentId}"));

    /// <summary>
    /// Decodes a cursor, refusing one this ledger did not produce.
    /// </summary>
    /// <remarks>
    /// <b>A cursor that cannot be parsed is refused, not silently ignored.</b> Treating it as "no
    /// cursor" would answer with the first page, so a client paging with a corrupted cursor would
    /// read page one, receive a valid next cursor, and fetch page one again — forever, with nothing
    /// anywhere saying why. A refusal that names the problem is worth more than a fallback that hides
    /// it, which is the same conclusion as refusing an unenumerable `state` by name rather than
    /// defaulting the filter.
    /// </remarks>
    /// <exception cref="InvalidDecisionCursorException">The cursor is not one this ledger produced.</exception>
    private static (string? RecordedAt, string? AssessmentId) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return (null, null);
        }

        string decoded;

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException ex)
        {
            throw new InvalidDecisionCursorException(NotOurCursor, ex);
        }

        var parts = decoded.Split(CursorSeparator);

        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new InvalidDecisionCursorException(NotOurCursor);
        }

        return (parts[0], parts[1]);
    }

    private const string NotOurCursor =
        "The `after` cursor is not one this ledger issued. Pass back the `nextCursor` from the "
        + "previous page unchanged, or omit it to read from the newest decision.";
}

/// <summary>A listing cursor that this ledger did not produce.</summary>
/// <remarks>
/// Its own type rather than an <see cref="ArgumentException"/>, so the endpoint answers a bad cursor
/// with a specific refusal while a genuine programming error still surfaces as a failure rather than
/// being swallowed into a 400.
/// </remarks>
public sealed class InvalidDecisionCursorException : Exception
{
    public InvalidDecisionCursorException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
