namespace StyloMail.Desktop.Api.Contracts;

/// <summary>Safe, tenant-scoped proxy management data. Identity, endpoint and credential material are intentionally absent.</summary>
public sealed record ProxyAccountsResponse
{
    public required string TenantId { get; init; }
    public required IReadOnlyList<ProxyAccountResponse> Accounts { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}

public sealed record ProxyAccountResponse
{
    public required string AccountId { get; init; }
    public required string LoginKey { get; init; }
    public required bool Enabled { get; init; }
    public required string ProviderId { get; init; }
    public required string CredentialDiscriminator { get; init; }
    public required string EnrollmentState { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed record ProxySessionsResponse
{
    public required string TenantId { get; init; }
    public required string Scope { get; init; }
    public required string Availability { get; init; }
    public required IReadOnlyList<ProxySessionResponse> Sessions { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}

public sealed record ProxyListenersResponse { public required string TenantId { get; init; } public required IReadOnlyList<ProxyListenerResponse> Listeners { get; init; } }

public sealed record ProxySessionResponse
{
    public required string SessionId { get; init; }
    public string? AccountId { get; init; }
    public required string Protocol { get; init; }
    public required string Direction { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? AuthenticatedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Outcome { get; init; }
    /// <summary>Null until byte instrumentation exists; zero is a future measured value.</summary>
    public long? ClientBytes { get; init; }
    public long? BackendBytes { get; init; }
    public string? BoundReason { get; init; }
}

public sealed record ProxyOperationsResponse
{
    public required string TenantId { get; init; }
    /// <summary>Slice 3's only accepted named observation window: last_24_hours.</summary>
    public required string Window { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required string Availability { get; init; }
    public required IReadOnlyList<ProxyListenerResponse> Listeners { get; init; }
    public required ProxyCounters Counters { get; init; }
    public required string Provenance { get; init; }
}

public sealed record ProxyCounters { public long? LiveSessions { get; init; } public long? Opened { get; init; } public long? Closed { get; init; } public long? Faulted { get; init; } }
public sealed record ProxyListenerResponse { public required string ListenerId { get; init; } public required string Protocol { get; init; } public required bool Enabled { get; init; } public required string State { get; init; } public required string TlsPolicy { get; init; } public required bool Draining { get; init; } public int? BoundPort { get; init; } public DateTimeOffset? ObservedAt { get; init; } public string? FaultReason { get; init; } public string? SafeDetail { get; init; } }
