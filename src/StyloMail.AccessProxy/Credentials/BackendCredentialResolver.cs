namespace StyloMail.AccessProxy.Credentials;

/// <summary>
/// Produces the backend authentication exchange for an account, whatever kind of credential it holds.
/// </summary>
/// <remarks>
/// This is the only place a stored credential is decrypted, and the only place a discriminator is
/// consulted. It is deliberately the narrowest possible funnel: a session driver asks for "how do I
/// authenticate to the backend as this account" and receives an opaque
/// <see cref="IBackendAuthenticator"/>, so it has no way to behave differently for an app password
/// than for a refresh token even accidentally.
///
/// <para>
/// <b>Every failure here fails closed</b> and is reported as
/// <see cref="CredentialUnavailableException"/>. That covers a missing credential, a revoked one, a
/// discriminator this build does not serve, ciphertext that fails authentication, and a provider
/// that cannot produce a token. There is no path that returns "no authenticator, carry on
/// unauthenticated", and no path that retries.
/// </para>
/// </remarks>
public sealed class BackendCredentialResolver
{
    private readonly IBackendCredentialStore _store;
    private readonly ISecretProtector _protector;
    private readonly BackendCredentialProviders _providers;
    private readonly TimeProvider _timeProvider;

    public BackendCredentialResolver(
        IBackendCredentialStore store,
        ISecretProtector protector,
        BackendCredentialProviders providers,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Builds the authenticator for <paramref name="accountId"/> against <paramref name="protocol"/>.
    /// </summary>
    /// <exception cref="CredentialUnavailableException">The credential cannot be used. Never retried.</exception>
    public async ValueTask<IBackendAuthenticator> ResolveAsync(
        string accountId,
        BackendProtocol protocol,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);

        var record = await _store.FindAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialUnavailableException(
                $"Account '{accountId}' has no backend credential enrolled.");

        if (record.Revoked)
        {
            // Spec §9.5 risk 4. Checked before decryption so a revoked credential is never even
            // unwrapped, and so revocation works even if the key that protected it is gone.
            throw new CredentialUnavailableException(
                $"The backend credential for account '{accountId}' has been revoked.");
        }

        var provider = _providers.TryGet(record.Discriminator)
            ?? throw new CredentialUnavailableException(
                $"No credential provider is registered for discriminator '{record.Discriminator}' " +
                $"(registered: {string.Join(", ", _providers.Discriminators)}). Refusing to guess at the " +
                "credential's shape.");

        var binding = new SecretBinding(record.TenantId, record.AccountId, record.Discriminator);
        using var secret = _protector.Unprotect(record.ProtectedSecret, record.ProtectionKeyId, binding);

        var context = new BackendCredentialContext
        {
            Record = record,
            Secret = secret,
            Protocol = protocol,
            TimeProvider = _timeProvider,
        };

        // The provider must not retain the plaintext past this call; `secret` is disposed as the
        // using scope ends, zeroing the bytes the provider was handed.
        return await provider.CreateAuthenticatorAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
