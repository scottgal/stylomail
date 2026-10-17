using StyloMail.AccessProxy.Credentials;

namespace StyloMail.AccessProxy.Accounts;

/// <summary>
/// The durable account row prepared by the access layer for atomic enrolment.
/// </summary>
/// <remarks>
/// <see cref="LoginKey"/> is a tenant-keyed, normalized lookup pseudonym. It is deliberately not
/// the login a user typed. Persistence can therefore enforce tenant-local uniqueness without storing
/// a raw address just to authenticate a client.
/// </remarks>
public sealed record ProxyAccountEnrollment
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required string LoginKey { get; init; }

    public required PasswordHash PasswordHash { get; init; }

    public required string BackendAccountId { get; init; }

    public required bool Enabled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The encrypted backend-credential row prepared by the access layer for atomic enrolment.
/// </summary>
/// <remarks>
/// This type intentionally has no plaintext-secret property. The access layer protects a
/// <see cref="SecretValue"/> before creating it; persistence receives ciphertext and never needs a
/// path that could write a credential in plaintext.
/// </remarks>
public sealed record ProxyBackendCredentialEnrollment
{
    public required string TenantId { get; init; }

    public required string BackendAccountId { get; init; }

    public required string ProviderId { get; init; }

    public required string Discriminator { get; init; }

    public required byte[] ProtectedSecret { get; init; }

    public required string ProtectionKeyId { get; init; }

    public required string BackendUsername { get; init; }

    public string? GrantedScopes { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The two durable rows that must commit or roll back together.</summary>
public sealed record ProxyEnrollment
{
    public required ProxyAccountEnrollment Account { get; init; }

    public required ProxyBackendCredentialEnrollment Credential { get; init; }
}

/// <summary>The declared reasons an enrolment write can be rejected.</summary>
public enum ProxyEnrollmentConflict
{
    DuplicateLoginKey = 0,
    DuplicateAccountId = 1,
    TenantMismatch = 2,
    /// <summary>The addressed tenant-scoped proxy account does not exist.</summary>
    AccountNotFound = 3,
    /// <summary>The addressed tenant-scoped backend credential does not exist.</summary>
    CredentialNotFound = 4,
    /// <summary>An existing backend credential is absent from service or has been revoked.</summary>
    BackendCredentialUnavailable = 5,
}

/// <summary>A safe, typed persistence rejection that contains no login or credential material.</summary>
public sealed class ProxyEnrollmentConflictException : Exception
{
    public ProxyEnrollmentConflictException(ProxyEnrollmentConflict conflict)
        : base($"The proxy enrolment was rejected: {conflict}.")
        => Conflict = conflict;

    public ProxyEnrollmentConflict Conflict { get; }
}

/// <summary>Changes whether an account can establish new proxy sessions.</summary>
public sealed record ProxyAccountStateChange
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required bool Enabled { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Marks a tenant-scoped backend credential unusable without deleting its audit state.</summary>
public sealed record ProxyBackendCredentialRevocation
{
    public required string TenantId { get; init; }

    public required string BackendAccountId { get; init; }

    public required DateTimeOffset RevokedAt { get; init; }
}

/// <summary>Replaces only a credential's protected bytes and key identifier in one atomic write.</summary>
public sealed record ProxyBackendCredentialRewrap
{
    public required string TenantId { get; init; }

    public required string BackendAccountId { get; init; }

    public required byte[] ProtectedSecret { get; init; }

    public required string ProtectionKeyId { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Atomic persistence boundary for a proxy account and its backend credential.
/// </summary>
/// <remarks>
/// Implementations must insert both rows in one transaction, reject tenant mismatch and duplicate
/// tenant/login-key or account identities, and never commit an account whose referenced credential is
/// absent or revoked. The Host owns the SQLite implementation; this project owns these semantics.
/// </remarks>
public interface IProxyEnrollmentPersistence
{
    /// <exception cref="ProxyEnrollmentConflictException">A declared enrolment invariant was rejected.</exception>
    ValueTask EnrolAsync(ProxyEnrollment enrollment, CancellationToken cancellationToken);

    /// <summary>Atomically changes the enabled state for a tenant-scoped account.</summary>
    ValueTask SetAccountEnabledAsync(ProxyAccountStateChange change, CancellationToken cancellationToken);

    /// <summary>Atomically marks a tenant-scoped backend credential revoked.</summary>
    ValueTask RevokeBackendCredentialAsync(
        ProxyBackendCredentialRevocation revocation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically replaces an existing account and its active backend credential during re-enrolment.
    /// </summary>
    /// <exception cref="ProxyEnrollmentConflictException">The replacement violates an enrolment invariant.</exception>
    ValueTask ReenrolAsync(ProxyEnrollment enrollment, CancellationToken cancellationToken);

    /// <summary>Atomically persists a ciphertext/key-id rewrap for an active backend credential.</summary>
    ValueTask RewrapBackendCredentialAsync(
        ProxyBackendCredentialRewrap rewrap,
        CancellationToken cancellationToken);
}

/// <summary>The access-layer service shape that converts enrolment secrets into durable records.</summary>
/// <remarks>
/// The service hashes the client secret, protects the backend secret, clears both values, and calls
/// <see cref="IProxyEnrollmentPersistence"/> once. It never exposes either secret in a return value.
/// </remarks>
public interface IProxyEnrollmentService
{
    ValueTask EnrolAsync(ProxyEnrollmentRequest request, CancellationToken cancellationToken);
}

/// <summary>Secret-bearing input accepted only at the trusted enrolment boundary.</summary>
public sealed record ProxyEnrollmentRequest
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    /// <summary>The user-facing login, used only to derive a tenant-keyed lookup pseudonym.</summary>
    public required string Login { get; init; }

    /// <summary>Disposed and cleared by the enrolment service.</summary>
    public required SecretValue ClientPassword { get; init; }

    public required string BackendAccountId { get; init; }

    public required string ProviderId { get; init; }

    public required string Discriminator { get; init; }

    public required string BackendUsername { get; init; }

    public string? GrantedScopes { get; init; }

    /// <summary>Disposed and cleared by the enrolment service.</summary>
    public required SecretValue BackendSecret { get; init; }
}
