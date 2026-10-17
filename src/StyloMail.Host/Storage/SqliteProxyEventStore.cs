using System.Globalization;
using Microsoft.Data.Sqlite;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Storage;

/// <summary>
/// Durable, tenant-scoped proxy telemetry: listener lifecycle, session lifecycle and structural
/// threading facts.
/// </summary>
/// <remarks>
/// <para>
/// This is the Host's implementation of access's <see cref="IProxyListenerEventSink"/>, so access
/// emits a fact through its own published contract and never reads this schema. Reads are a Host
/// concern and live behind <see cref="IProxyTelemetryReader"/>.
/// </para>
/// <para>
/// <b>Empty and not-produced are different answers.</b> A tenant with no listener event has never
/// had telemetry produced for it, so "no live sessions" there is <c>not_produced</c> rather than
/// zero. Once any listener event exists the stream is readable, and an empty window is an honest
/// zero. That distinction is why the routes ask this store whether telemetry has ever existed
/// instead of inferring it from a count that happens to be nothing.
/// </para>
/// </remarks>
public sealed class SqliteProxyEventStore : IProxyListenerEventSink, IProxyThreadingEventSink,
    IProxyTelemetryReader, IProxySessionAuthenticator
{
    private readonly HostDatabase _database;

    public SqliteProxyEventStore(HostDatabase database) => _database = database;

    public ValueTask PublishAsync(ProxyListenerEvent listenerEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listenerEvent);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO proxy_listener_event "
            + "(tenant_id,protocol,kind,occurred_at,bound_port,fault_reason,safe_detail) "
            + "VALUES ($tenant,$protocol,$kind,$at,$port,$fault,$detail);";
        command.Parameters.AddWithValue("$tenant", listenerEvent.TenantId);
        command.Parameters.AddWithValue("$protocol", listenerEvent.Protocol.ToString());
        command.Parameters.AddWithValue("$kind", listenerEvent.Kind.ToString());
        command.Parameters.AddWithValue("$at", Timestamp(listenerEvent.Timestamp));
        command.Parameters.AddWithValue("$port", (object?)listenerEvent.BoundPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$fault", listenerEvent.FaultReason.ToString());
        command.Parameters.AddWithValue("$detail", (object?)listenerEvent.SafeDetail ?? DBNull.Value);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(ProxySessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO proxy_session_event "
            + "(tenant_id,session_id,account_id,protocol,direction,kind,occurred_at,outcome,"
            + "client_bytes,backend_bytes,bound_reason) "
            + "VALUES ($tenant,$session,$account,$protocol,$direction,$kind,$at,$outcome,$client,$backend,$bound);";
        command.Parameters.AddWithValue("$tenant", sessionEvent.TenantId);
        command.Parameters.AddWithValue("$session", sessionEvent.SessionId);
        command.Parameters.AddWithValue("$account", (object?)sessionEvent.AccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$protocol", sessionEvent.Protocol.ToString());
        command.Parameters.AddWithValue("$direction", sessionEvent.Direction.ToString());
        command.Parameters.AddWithValue("$kind", sessionEvent.Kind.ToString());
        command.Parameters.AddWithValue("$at", Timestamp(sessionEvent.Timestamp));
        command.Parameters.AddWithValue("$outcome", (object?)sessionEvent.Outcome?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$client", sessionEvent.ClientBytes);
        command.Parameters.AddWithValue("$backend", sessionEvent.BackendBytes);
        command.Parameters.AddWithValue("$bound", sessionEvent.BoundReason.ToString());
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(ProxyThreadingEvent threadingEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(threadingEvent);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO proxy_threading_event "
            + "(tenant_id,account_id,protocol,message_token,thread_token,reference_depth,occurred_at) "
            + "VALUES ($tenant,$account,$protocol,$message,$thread,$depth,$at);";
        command.Parameters.AddWithValue("$tenant", threadingEvent.TenantId);
        command.Parameters.AddWithValue("$account", threadingEvent.AccountId);
        command.Parameters.AddWithValue("$protocol", threadingEvent.Protocol.ToString());
        command.Parameters.AddWithValue("$message", threadingEvent.MessageToken);
        command.Parameters.AddWithValue("$thread", threadingEvent.ThreadToken);
        command.Parameters.AddWithValue("$depth", threadingEvent.ReferenceDepth);
        command.Parameters.AddWithValue("$at", Timestamp(threadingEvent.Timestamp));
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkAuthenticatedAsync(
        string tenantId,
        string sessionId,
        string accountId,
        DateTimeOffset authenticatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // Update the existing opened row in place. "The newest event is Opened" is what makes the
        // session live, so a session that closed before authentication is not resurrected by a late
        // callback, and no second opened event is created.
        command.CommandText =
            "UPDATE proxy_session_event SET account_id=$account,authenticated_at=$at "
            + "WHERE id=(SELECT MAX(x.id) FROM proxy_session_event x "
            + "WHERE x.tenant_id=$tenant AND x.session_id=$session) "
            + "AND tenant_id=$tenant AND kind='Opened';";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$at", Timestamp(authenticatedAt));
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> HasListenerTelemetryAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Exists("proxy_listener_event", tenantId));
    }

    public ValueTask<bool> HasThreadingTelemetryAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Exists("proxy_threading_event", tenantId));
    }

    public ValueTask<IReadOnlyList<ProxyLiveSession>> ListLiveSessionsAsync(
        string tenantId,
        int limit,
        string? afterSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // A session is live when its newest event is the opening one. The correlated max is on the
        // opaque session id rather than on time, so two events in the same millisecond cannot make
        // a closed session look live.
        command.CommandText =
            "SELECT s.session_id,s.account_id,s.protocol,s.direction,s.occurred_at,s.authenticated_at,"
            + "s.client_bytes,s.backend_bytes "
            + "FROM proxy_session_event s "
            + "WHERE s.tenant_id=$tenant AND s.kind='Opened' "
            + "AND s.id=(SELECT MAX(x.id) FROM proxy_session_event x "
            + "WHERE x.tenant_id=s.tenant_id AND x.session_id=s.session_id) "
            + "AND ($after IS NULL OR s.session_id > $after) "
            + "ORDER BY s.session_id LIMIT $limit;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$after", (object?)afterSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var rows = new List<ProxyLiveSession>();
        while (reader.Read())
        {
            rows.Add(new ProxyLiveSession
            {
                SessionId = reader.GetString(0),
                AccountId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Protocol = ParseProtocol(reader.GetString(2)),
                Direction = Enum.Parse<ProxySessionDirection>(reader.GetString(3), ignoreCase: true),
                StartedAt = ParseTimestamp(reader.GetString(4)),
                AuthenticatedAt = reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                ClientBytes = reader.GetInt64(6),
                BackendBytes = reader.GetInt64(7),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ProxyLiveSession>>(rows);
    }

    public ValueTask<ProxyEventCounters> CountAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT "
            + "(SELECT COUNT(*) FROM proxy_session_event s WHERE s.tenant_id=$tenant AND s.kind='Opened' "
            + " AND s.id=(SELECT MAX(x.id) FROM proxy_session_event x "
            + " WHERE x.tenant_id=s.tenant_id AND x.session_id=s.session_id)),"
            + "(SELECT COUNT(*) FROM proxy_session_event s WHERE s.tenant_id=$tenant AND s.kind='Opened' "
            + " AND s.occurred_at >= $from AND s.occurred_at < $to),"
            + "(SELECT COUNT(*) FROM proxy_session_event s WHERE s.tenant_id=$tenant AND s.kind='Closed' "
            + " AND s.occurred_at >= $from AND s.occurred_at < $to),"
            + "(SELECT COUNT(*) FROM proxy_listener_event l WHERE l.tenant_id=$tenant AND l.kind='Faulted' "
            + " AND l.occurred_at >= $from AND l.occurred_at < $to);";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$from", Timestamp(from));
        command.Parameters.AddWithValue("$to", Timestamp(to));

        using var reader = command.ExecuteReader();
        reader.Read();
        return ValueTask.FromResult(new ProxyEventCounters(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
    }

    public ValueTask<ProxyListenerFact?> LatestListenerFactAsync(
        string tenantId,
        ProxyListenerProtocol protocol,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind,fault_reason,safe_detail,occurred_at,bound_port FROM proxy_listener_event "
            + "WHERE tenant_id=$tenant AND protocol=$protocol ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$protocol", protocol.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ValueTask.FromResult<ProxyListenerFact?>(null);
        }

        return ValueTask.FromResult<ProxyListenerFact?>(new ProxyListenerFact(
            Enum.Parse<ProxyListenerEventKind>(reader.GetString(0), ignoreCase: true),
            Enum.Parse<ProxyListenerFaultReason>(reader.GetString(1), ignoreCase: true),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt32(4)));
    }

    public ValueTask<IReadOnlyList<ProxyConversationRow>> ListConversationsAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        string? afterThreadToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.thread_token,MAX(t.reference_depth),MAX(t.occurred_at),"
            + "GROUP_CONCAT(t.account_id,'|'),"
            + "(SELECT x.account_id FROM proxy_threading_event x "
            + " WHERE x.tenant_id=t.tenant_id AND x.thread_token=t.thread_token "
            + " ORDER BY x.occurred_at DESC,x.id DESC LIMIT 1) "
            + "FROM proxy_threading_event t "
            + "WHERE t.tenant_id=$tenant AND t.occurred_at >= $from AND t.occurred_at < $to "
            + "AND ($after IS NULL OR t.thread_token > $after) "
            + "GROUP BY t.thread_token ORDER BY t.thread_token LIMIT $limit;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$from", Timestamp(from));
        command.Parameters.AddWithValue("$to", Timestamp(to));
        command.Parameters.AddWithValue("$after", (object?)afterThreadToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);

        using var reader = command.ExecuteReader();
        var rows = new List<ProxyConversationRow>();
        while (reader.Read())
        {
            var participants = (reader.IsDBNull(3) ? string.Empty : reader.GetString(3))
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            rows.Add(new ProxyConversationRow
            {
                ThreadToken = reader.GetString(0),
                ReferenceDepth = reader.GetInt32(1),
                LastActivityAt = ParseTimestamp(reader.GetString(2)),
                AccountId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ParticipantTokens = participants,
                Availability = "available",
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ProxyConversationRow>>(rows);
    }

    public ValueTask<bool> HasConversationAsync(
        string tenantId,
        string threadToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM proxy_threading_event WHERE tenant_id=$tenant AND thread_token=$thread LIMIT 1;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$thread", threadToken);
        return ValueTask.FromResult(command.ExecuteScalar() is not null);
    }

    public ValueTask<IReadOnlyList<ProxyThreadPoint>> ListThreadPointsAsync(
        string tenantId,
        string threadToken,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT occurred_at,message_token FROM proxy_threading_event "
            + "WHERE tenant_id=$tenant AND thread_token=$thread "
            + "AND occurred_at >= $from AND occurred_at < $to ORDER BY occurred_at,id;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$thread", threadToken);
        command.Parameters.AddWithValue("$from", Timestamp(from));
        command.Parameters.AddWithValue("$to", Timestamp(to));

        using var reader = command.ExecuteReader();
        var points = new List<ProxyThreadPoint>();
        while (reader.Read())
        {
            // The transport fact exists; the semantic measurement does not. The point is emitted
            // rather than omitted so a gap in the series is an explicit not_produced observation,
            // never something a renderer can draw as a straight line through.
            points.Add(new ProxyThreadPoint
            {
                ObservedAt = ParseTimestamp(reader.GetString(0)),
                TurnToken = reader.GetString(1),
                ToneScore = null,
                WarningMarkers = [],
                DimensionIds = [],
                Availability = "not_produced",
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ProxyThreadPoint>>(points);
    }

    private bool Exists(string table, string tenantId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {table} WHERE tenant_id=$tenant LIMIT 1;";
        command.Parameters.AddWithValue("$tenant", tenantId);
        return command.ExecuteScalar() is not null;
    }

    private static ProxyListenerProtocol ParseProtocol(string value)
        => Enum.Parse<ProxyListenerProtocol>(value, ignoreCase: true);

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
