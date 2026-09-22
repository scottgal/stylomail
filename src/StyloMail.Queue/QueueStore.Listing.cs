using System.Globalization;
using Microsoft.Data.Sqlite;
using StyloMail.Core;

namespace StyloMail.Queue;

/// <summary>
/// Listing and paging over queue items.
/// </summary>
/// <remarks>
/// Split from the main store file because it is a read model with its own shape rather than more
/// queue mechanics, and because a consumer that needs to enumerate held mail should be able to find
/// it without reading the accept path.
/// </remarks>
public sealed partial class QueueStore
{
    private const string CursorPrefix = "v1";

    /// <summary>
    /// Field separator for the cursor.
    /// </summary>
    /// <remarks>
    /// <b>Not a colon.</b> An ISO-8601 timestamp is full of them, so a colon-separated cursor splits
    /// the timestamp itself and yields a truncated, unparseable date. Tilde is unreserved in RFC
    /// 3986, so it also survives a round trip through a URL query parameter unencoded, and neither
    /// an ISO timestamp nor a <c>q-</c> prefixed id contains one.
    /// </remarks>
    private const char CursorSeparator = '~';

    /// <summary>
    /// Lists a tenant's items awaiting a decision, newest first, with per-recipient dispositions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyset paging, not OFFSET.</b> Items move between states constantly, and an offset window
    /// over a live queue silently skips or repeats rows as its contents shift underneath. The cursor
    /// names the last row returned, so the next page starts after <em>that</em> item regardless of
    /// what changed in between, a reviewer paging through held mail sees each item once.
    /// </para>
    /// <para>
    /// Ordering is <c>(created_at DESC, queue_id DESC)</c>. The id is the tiebreaker because
    /// timestamps are not unique, several messages can be accepted in the same instant, and
    /// without a total order a keyset cursor can loop or drop rows at a page boundary.
    /// </para>
    /// </remarks>
    public async Task<QueueListingPage> ListAsync(
        QueueListingQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var limit = Math.Clamp(query.Limit, 1, QueueListingLimits.MaxPageSize);
        var now = _options.TimeProvider.GetUtcNow();
        var cursor = DecodeCursor(query.After);

        // Two fixed parameters rather than a variable-length IN list or a JSON1 helper: the filter
        // is a closed set of three cases, and this keeps the SQL static and the statement
        // preparable without depending on which extensions a given SQLite build was compiled with.
        var (stateA, stateB) = query.Filter switch
        {
            QueueListingFilter.Held => (DeliveryState.Held, DeliveryState.Held),
            QueueListingFilter.Quarantined => (DeliveryState.Quarantined, DeliveryState.Quarantined),
            _ => (DeliveryState.Held, DeliveryState.Quarantined),
        };

        await using var connection = OpenConnection();

        // Rows are held as PAIRS. Taking the cursor's timestamp from a separate running variable was
        // a real bug: the loop's last write is the *probe* row, so the cursor paired the probe's
        // `created_at` with the last *kept* row's id. Ordering is `created_at DESC`, so the probe's
        // timestamp is older — and every row between the two was skipped on the next page, silently,
        // while the listing reported itself complete. Both halves must come from one row.
        var rows = new List<(string QueueId, string CreatedAt)>(limit + 1);

        using (var cmd = connection.CreateCommand())
        {
            // One extra row tells us whether a further page exists without a second COUNT query.
            cmd.CommandText =
                """
                SELECT i.queue_id, i.created_at
                  FROM queue_item i
                 WHERE i.tenant_id = $tenant
                   AND EXISTS (
                       SELECT 1 FROM queue_recipient r
                        WHERE r.queue_id = i.queue_id
                          AND r.state IN ($stateA, $stateB))
                   AND ($afterAt IS NULL
                        OR i.created_at < $afterAt
                        OR (i.created_at = $afterAt AND i.queue_id < $afterId))
                 ORDER BY i.created_at DESC, i.queue_id DESC
                 LIMIT $limit;
                """;

            cmd.Parameters.AddWithValue("$tenant", query.TenantId);
            cmd.Parameters.AddWithValue("$stateA", (int)stateA);
            cmd.Parameters.AddWithValue("$stateB", (int)stateB);
            cmd.Parameters.AddWithValue("$afterAt", cursor is null ? DBNull.Value : ToDb(cursor.Value.CreatedAt));
            cmd.Parameters.AddWithValue("$afterId", cursor is null ? DBNull.Value : cursor.Value.QueueId);
            cmd.Parameters.AddWithValue("$limit", limit + 1);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(FromDb(rows[^1].CreatedAt) ?? now, rows[^1].QueueId);
        }

        var items = new List<QueueItem>(rows.Count);
        foreach (var id in rows.Select(r => r.QueueId))
        {
            if (ReadItem(connection, transaction: null, id, now) is { } item)
            {
                items.Add(item);
            }
        }

        return new QueueListingPage { Items = items, NextCursor = nextCursor };
    }

    /// <summary>
    /// Reads one item's per-recipient states, scoped to a tenant.
    /// </summary>
    /// <remarks>
    /// Convenience for a reviewer surface that has a queue id in hand. Equivalent to
    /// <see cref="GetItemAsync"/>, present only so a caller can fetch a single item the same way it
    /// fetched the page that named it.
    /// </remarks>
    public Task<QueueItem?> GetItemForTenantAsync(
        string queueId,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return GetItemAsync(queueId, tenantId, cancellationToken);
    }

    private static string EncodeCursor(DateTimeOffset createdAt, string queueId)
        => $"{CursorPrefix}{CursorSeparator}{ToDb(createdAt)}{CursorSeparator}{queueId}";

    /// <summary>
    /// Parses a cursor, rejecting anything malformed rather than silently starting from the top.
    /// </summary>
    /// <remarks>
    /// Silently treating a bad cursor as "no cursor" would restart the listing, which on a review
    /// surface looks like the first page repeating forever. Failing loudly makes a client bug
    /// visible at the point it happens.
    /// </remarks>
    private static (DateTimeOffset CreatedAt, string QueueId)? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var parts = cursor.Split(CursorSeparator);

        // TryParse, not the strict FromDb used for database rows: a cursor is caller-supplied, so a
        // malformed one is a client bug to report, not a FormatException to leak. A corrupt value in
        // the database is a different situation and still throws.
        if (parts.Length != 3
            || !string.Equals(parts[0], CursorPrefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[2])
            || !DateTimeOffset.TryParse(
                parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
        {
            throw new ArgumentException(
                $"'{cursor}' is not a valid queue listing cursor. Echo the NextCursor value from the " +
                "previous page unchanged; do not construct one.",
                nameof(cursor));
        }

        return (createdAt, parts[2]);
    }
}
