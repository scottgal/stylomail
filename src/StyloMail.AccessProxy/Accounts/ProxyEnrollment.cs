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
    ValueTask EnrolAsync(ProxyEnrollment enrollment, CancellationToken cancellationToken);
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
