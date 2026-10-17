using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>A live session's safe management projection.</summary>
public sealed record ProxyLiveSession
{
    public required string SessionId { get; init; }

    public string? AccountId { get; init; }

    public required ProxyListenerProtocol Protocol { get; init; }

    public required ProxySessionDirection Direction { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Set once the client and backend both authenticated; null means it never did.</summary>
    public DateTimeOffset? AuthenticatedAt { get; init; }

    public long ClientBytes { get; init; }

    public long BackendBytes { get; init; }
}

/// <summary>Records the moment a live session authenticated, without opening a second session.</summary>
/// <remarks>
/// The protocol session is the only owner of the legitimate account id. This write updates the
/// existing opened event in place; a failed write is swallowed by the caller because telemetry must
/// never be able to drop a user's session.
/// </remarks>
public interface IProxySessionAuthenticator
{
    ValueTask MarkAuthenticatedAsync(
        string tenantId,
        string sessionId,
        string accountId,
        DateTimeOffset authenticatedAt,
        CancellationToken cancellationToken);
}

/// <summary>Counts over the named window. Null means "not produced", never zero.</summary>
public sealed record ProxyEventCounters(long? LiveSessions, long? Opened, long? Closed, long? Faulted);

/// <summary>One structural conversation row, grouped from non-reversible threading tokens.</summary>
public sealed record ProxyConversationRow
{
    public required string ThreadToken { get; init; }

    public required string AccountId { get; init; }

    public required IReadOnlyList<string> ParticipantTokens { get; init; }

    public required int ReferenceDepth { get; init; }

    public required DateTimeOffset LastActivityAt { get; init; }

    /// <summary>Whether the structural facts for this row were produced.</summary>
    public required string Availability { get; init; }
}

/// <summary>One measured turn in a conversation's series.</summary>
public sealed record ProxyThreadPoint
{
    public required DateTimeOffset ObservedAt { get; init; }

    public required string TurnToken { get; init; }

    /// <summary>Null because no semantic measurement is linked to this transport event yet.</summary>
    public double? ToneScore { get; init; }

    public IReadOnlyList<string> WarningMarkers { get; init; } = [];

    public IReadOnlyList<string> DimensionIds { get; init; } = [];

    /// <summary><c>available</c> or <c>not_produced</c>. A missing point is explicit, never omitted.</summary>
    public required string Availability { get; init; }

    /// <summary>
    /// The closed reason vocabulary from the analysis outcome: None, NoAnalyserConfigured,
    /// UnreadableMessage or UnsupportedContent. A NotProduced point always names one.
    /// </summary>
    public required string AvailabilityReason { get; init; }

    /// <summary>Equals the marker count on an available point; zero is a measured clean message.</summary>
    public required int MarkerCount { get; init; }
}

/// <summary>Accepts deterministic warning outcomes and their markers from the analysis tap.</summary>
/// <remarks>
/// An outcome is recorded for every attempted message, including a clean one (Available with
/// MarkerCount 0). Markers alone cannot distinguish a clean message from an unanalysed one, so the
/// outcome is the evidence that analysis ran.
/// </remarks>
public interface IProxyWarningEventSink
{
    ValueTask RecordOutcomeAsync(
        ProxyWarningAnalysisOutcome outcome,
        CancellationToken cancellationToken);

    ValueTask RecordMarkerAsync(
        ProxyDeterministicWarningEvent marker,
        CancellationToken cancellationToken);
}

/// <summary>The latest listener fact recorded for a protocol.</summary>
public sealed record ProxyListenerFact(
    ProxyListenerEventKind Kind,
    ProxyListenerFaultReason FaultReason,
    string? SafeDetail,
    DateTimeOffset ObservedAt,
    int? BoundPort);

/// <summary>Freshness and availability of the proxy telemetry the routes report on.</summary>
public interface IProxyTelemetryReader
{
    /// <summary>Whether any listener event has ever been recorded for this tenant.</summary>
    ValueTask<bool> HasListenerTelemetryAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Whether any structural threading event has ever been recorded for this tenant.</summary>
    ValueTask<bool> HasThreadingTelemetryAsync(string tenantId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProxyLiveSession>> ListLiveSessionsAsync(
        string tenantId,
        int limit,
        string? afterSessionId,
        CancellationToken cancellationToken);

    ValueTask<ProxyEventCounters> CountAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    ValueTask<ProxyListenerFact?> LatestListenerFactAsync(
        string tenantId,
        ProxyListenerProtocol protocol,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProxyConversationRow>> ListConversationsAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        string? afterThreadToken,
        CancellationToken cancellationToken);

    ValueTask<bool> HasConversationAsync(
        string tenantId,
        string threadToken,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProxyThreadPoint>> ListThreadPointsAsync(
        string tenantId,
        string threadToken,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <summary>Accepts structural threading events derived from bounded message headers.</summary>
/// <remarks>
/// A Host-owned seam rather than an access-owned one because access emits the event and the Host
/// persists it. It carries no header value: only non-reversible tokens, depth and time.
/// </remarks>
public interface IProxyThreadingEventSink
{
    ValueTask PublishAsync(ProxyThreadingEvent threadingEvent, CancellationToken cancellationToken);
}
