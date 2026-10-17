namespace StyloMail.Host.Proxy;

/// <summary>A safe, management-facing projection of one proxy account.</summary>
/// <remarks>
/// No raw login, backend username, verifier, ciphertext, key id, scopes or secret-adjacent field is
/// present. <see cref="LoginKey"/> is the tenant-keyed pseudonym the durable row is looked up by, so
/// two tenants that use the same address cannot be correlated from this surface.
/// </remarks>
public sealed record ProxyAccountRecord
{
    public required string AccountId { get; init; }

    public required string LoginKey { get; init; }

    public required bool Enabled { get; init; }

    public required string ProviderId { get; init; }

    public required string CredentialDiscriminator { get; init; }

    /// <summary><c>active</c> or <c>revoked</c>. A disabled account is still an active enrolment.</summary>
    public required string EnrollmentState { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The backend credential this account authenticates with. Internal; never emitted.</summary>
    public required string BackendAccountId { get; init; }
}

/// <summary>A page of accounts, ordered stably by <see cref="ProxyAccountRecord.AccountId"/>.</summary>
public sealed record ProxyAccountPage(IReadOnlyList<ProxyAccountRecord> Accounts, bool HasMore);

/// <summary>
/// The Host-side read model over the durable proxy account rows.
/// </summary>
/// <remarks>
/// Deliberately separate from the access-owned <c>IStyloMailAccountStore</c>: that seam answers the
/// authentication question ("which account is this login?") and has no business exposing a listing.
/// Both are implemented over the same table, so a route can never read an account authentication
/// would not see.
/// </remarks>
public interface IProxyAccountDirectory
{
    ValueTask<ProxyAccountRecord?> GetAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken);

    /// <summary>Lists accounts after <paramref name="afterAccountId"/>, ordered by account id.</summary>
    ValueTask<ProxyAccountPage> ListAsync(
        string tenantId,
        int limit,
        string? afterAccountId,
        CancellationToken cancellationToken);
}
