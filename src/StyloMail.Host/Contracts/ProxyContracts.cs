namespace StyloMail.Host.Contracts;

/// <summary>
/// Creates one proxy account for the authenticated tenant.
/// </summary>
/// <remarks>
/// <b>No tenant and no account id are accepted.</b> The tenant is the authenticated principal's, and
/// the id is minted by the Host. A caller-chosen id would let two tenants collide and would make an
/// opaque handle into something a client decided, which is exactly the property the contract
/// removed.
/// </remarks>
public sealed record ProxyAccountCreateRequest
{
    public string? Login { get; init; }

    /// <summary>The client's StyloMail password. Never stored, hashed by the enrolment service.</summary>
    public string? ClientPassword { get; init; }

    public string? BackendAccountId { get; init; }

    public string? ProviderId { get; init; }

    public string? CredentialDiscriminator { get; init; }

    public string? BackendUsername { get; init; }

    public string? GrantedScopes { get; init; }

    /// <summary>The backend credential. Protected before persistence and never echoed.</summary>
    public string? BackendSecret { get; init; }
}

/// <summary>The metadata-only answer to a successful enrolment.</summary>
public sealed record ProxyAccountCreatedResponse
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required bool Enabled { get; init; }

    public required string EnrollmentState { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProxyAccountsResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<ProxyAccountResponse> Accounts { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }
}

/// <summary>
/// One account as the management surface may see it.
/// </summary>
/// <remarks>
/// <see cref="LoginKey"/> is a tenant-keyed pseudonym, not the address a user typed.
/// <see cref="Enabled"/> is an operator state and says nothing about backend health: a disabled
/// account is still an active enrolment, and an enabled one can still have a revoked credential.
/// </remarks>
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

    /// <summary><c>available</c> or <c>not_produced</c>. Never read the latter as zero.</summary>
    public required string Availability { get; init; }

    public required IReadOnlyList<ProxySessionResponse> Sessions { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }
}

/// <summary>A live session's safe metadata. No peer address, command, header or payload.</summary>
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

    /// <summary>
    /// Null when no byte counter is wired. A measured zero remains distinct from "not measured",
    /// which a console must render as unknown rather than as a flat zero-byte session.
    /// </summary>
    public long? ClientBytes { get; init; }

    /// <summary>Null when no byte counter is wired. See <see cref="ClientBytes"/>.</summary>
    public long? BackendBytes { get; init; }

    public string? BoundReason { get; init; }
}

public sealed record ProxyListenersResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<ProxyListenerResponse> Listeners { get; init; }
}

/// <summary>
/// One listener's read-only facts plus its control state.
/// </summary>
/// <remarks>
/// <see cref="BindPort"/> is reported because an operator needs to know where clients connect.
/// Bind address, certificate reference and tenant binding are deliberately absent: they are
/// deployment configuration and are never mutable through this API.
/// </remarks>
public sealed record ProxyListenerResponse
{
    public required string ListenerId { get; init; }

    public required string Protocol { get; init; }

    public required int BindPort { get; init; }

    public required string TlsPolicy { get; init; }

    public required bool Enabled { get; init; }

    /// <summary><c>started</c>, <c>stopping</c>, <c>stopped</c>, <c>faulted</c> or <c>not_produced</c>.</summary>
    public required string State { get; init; }

    public string? FaultReason { get; init; }

    public string? SafeDetail { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>
    /// True while a disable has stopped accepting new sessions and existing ones are still running.
    /// The operator's confirmation copy must say "lets connected sessions finish", not "kills them".
    /// </summary>
    public required bool Draining { get; init; }
}

public sealed record ProxyOperationsResponse
{
    public required string TenantId { get; init; }

    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary><c>available</c> or <c>not_produced</c>.</summary>
    public required string Availability { get; init; }

    public required IReadOnlyList<ProxyListenerResponse> Listeners { get; init; }

    public required ProxyCountersResponse Counters { get; init; }

    public required string Provenance { get; init; }
}

/// <summary>Nullable counters: null is "not produced", never a readable zero.</summary>
public sealed record ProxyCountersResponse
{
    public long? LiveSessions { get; init; }

    public long? Opened { get; init; }

    public long? Closed { get; init; }

    public long? Faulted { get; init; }
}

public sealed record ProxyConversationsResponse
{
    public required string TenantId { get; init; }

    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required string Availability { get; init; }

    public required IReadOnlyList<ProxyConversationResponse> Conversations { get; init; }

    public string? NextCursor { get; init; }

    public required bool HasMore { get; init; }
}

/// <summary>
/// A structural thread row. It is a participant grouping, not a transcript: no subject, body,
/// header value or participant address is present.
/// </summary>
public sealed record ProxyConversationResponse
{
    public required string ThreadToken { get; init; }

    public required string AccountId { get; init; }

    public required IReadOnlyList<string> ParticipantTokens { get; init; }

    public required int ReferenceDepth { get; init; }

    public required DateTimeOffset LastActivityAt { get; init; }

    public required string Availability { get; init; }
}

public sealed record ProxyConversationSignalsResponse
{
    public required string TenantId { get; init; }

    public required string ThreadToken { get; init; }

    public required string Window { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required string Provenance { get; init; }

    public required IReadOnlyList<ProxySignalPointResponse> Points { get; init; }
}

/// <summary>One turn's measurement, or an explicit statement that it was not produced.</summary>
public sealed record ProxySignalPointResponse
{
    public required DateTimeOffset ObservedAt { get; init; }

    public required string TurnToken { get; init; }

    public double? ToneScore { get; init; }

    public required IReadOnlyList<string> WarningMarkers { get; init; }

    public required IReadOnlyList<string> DimensionIds { get; init; }

    /// <summary><c>available</c> or <c>not_produced</c>.</summary>
    public required string Availability { get; init; }
}
