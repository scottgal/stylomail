using System.Collections.Concurrent;

namespace StyloMail.AccessProxy.Credentials;

/// <summary>A credential being enrolled or replaced. The plaintext never leaves the store's care.</summary>
public sealed record BackendCredentialWrite
{
    public required string TenantId { get; init; }

    public required string AccountId { get; init; }

    public required string ProviderId { get; init; }

    /// <summary>What kind of credential this is. The provider registry must recognise it.</summary>
    public required string Discriminator { get; init; }

    public required string BackendUsername { get; init; }

    public string? GrantedScopes { get; init; }

    /// <summary>The credential itself. Disposed by the caller; not retained by the store.</summary>
    public required SecretValue Secret { get; init; }

    public required TimeProvider TimeProvider { get; init; }
}

/// <summary>
/// Where backend credentials live.
/// </summary>
/// <remarks>
/// <b>The store only accepts plaintext, and only stores ciphertext.</b> There is no method by which
/// a caller can hand this interface an already-encrypted blob or read one back out: writes take a
/// <see cref="SecretValue"/>, and the implementation encrypts before it persists. That makes
/// "a credential is never stored in plaintext" (spec §9.4 item 2) a property of the type rather
/// than a rule somebody has to remember, a caller cannot bypass the protector because the
/// protector is not reachable from the caller's side of this interface.
///
/// <para>
/// Reads return <see cref="BackendCredentialRecord"/>, which carries ciphertext. Decryption happens
/// at exactly one place, <see cref="BackendCredentialResolver"/>, at the moment a session is
/// established.
/// </para>
/// </remarks>
public interface IBackendCredentialStore
{
    /// <summary>The record for <paramref name="accountId"/>, or null when none is enrolled.</summary>
    ValueTask<BackendCredentialRecord?> FindAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Enrols or replaces the credential for an account, encrypting it on the way in.</summary>
    ValueTask<BackendCredentialRecord> StoreAsync(BackendCredentialWrite write, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the account's credential, returning false when none was enrolled.
    /// </summary>
    /// <remarks>
    /// The secondary revocation path spec §9.5 risk 4 requires: a user who revokes an app password
    /// in Google must be able to remove it here too, so that StyloMail stops presenting a credential
    /// the provider will reject. Revocation is a marker rather than a delete so that an incident can
    /// still tell "never enrolled" from "revoked at some point".
    /// </remarks>
    ValueTask<bool> RevokeAsync(string accountId, CancellationToken cancellationToken);
}

/// <summary>
/// An in-process credential store.
/// </summary>
/// <remarks>
/// <b>Not durable, and not the production store.</b> A restart loses every credential, which means
/// every session would fail closed until re-enrolment, correct, but useless. The durable SQLite
/// implementation belongs with the persistence layer and is deliberately not written here: this
/// project owns the seam and the contract, and inventing a second schema in another project's lane
/// would be worse than reporting the gap.
///
/// <para>
/// What this implementation <em>is</em> for: exercising the real protection path. It uses the
/// genuine <see cref="ISecretProtector"/>, so tests asserting "the stored bytes are not the
/// credential" are testing the code production would run, not a stand-in that trivially passes.
/// </para>
/// </remarks>
public sealed class InMemoryBackendCredentialStore : IBackendCredentialStore
{
    private readonly ConcurrentDictionary<string, BackendCredentialRecord> _records = new(StringComparer.Ordinal);
    private readonly ISecretProtector _protector;

    public InMemoryBackendCredentialStore(ISecretProtector protector)
        => _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public ValueTask<BackendCredentialRecord?> FindAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_records.TryGetValue(accountId, out var record) ? record : null);
    }

    public ValueTask<BackendCredentialRecord> StoreAsync(
        BackendCredentialWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        var binding = new SecretBinding(write.TenantId, write.AccountId, write.Discriminator);
        var protectedSecret = _protector.Protect(write.Secret, binding);
        var now = write.TimeProvider.GetUtcNow();

        var record = new BackendCredentialRecord
        {
            AccountId = write.AccountId,
            TenantId = write.TenantId,
            ProviderId = write.ProviderId,
            Discriminator = write.Discriminator,
            ProtectedSecret = protectedSecret.Ciphertext,
            ProtectionKeyId = protectedSecret.KeyId,
            BackendUsername = write.BackendUsername,
            GrantedScopes = write.GrantedScopes,
            CreatedAt = now,
            UpdatedAt = now,
            Revoked = false,
        };

        _records[write.AccountId] = record;
        return ValueTask.FromResult(record);
    }

    public ValueTask<bool> RevokeAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        while (_records.TryGetValue(accountId, out var existing))
        {
            // The record is immutable, so revocation is a replace. Retried against a concurrent
            // write rather than swallowed, so a revoke racing an enrolment still takes effect.
            var revoked = existing with { Revoked = true, UpdatedAt = existing.UpdatedAt };
            if (_records.TryUpdate(accountId, revoked, existing))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }
}
