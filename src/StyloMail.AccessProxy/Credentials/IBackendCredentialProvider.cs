namespace StyloMail.AccessProxy.Credentials;

/// <summary>
/// Everything a credential provider is given to produce a backend authentication exchange.
/// </summary>
/// <remarks>
/// The decrypted credential is handed over here and nowhere else. The provider takes ownership of
/// <see cref="Secret"/> and must dispose it; <see cref="BackendCredentialResolver"/> disposes it
/// after the provider returns, so a provider that copies the bytes out and keeps them is a bug the
/// resolver cannot catch — which is why copying out the plaintext is documented as forbidden rather
/// than merely discouraged.
///
/// <para>
/// <see cref="Protocol"/> is passed in because a provider may legitimately need to know which
/// protocol it is authenticating for — a mechanism set differs between IMAP and POP3 on the same
/// provider. It is <em>context</em>, not a branch the caller makes: the caller always passes the
/// protocol it is already speaking, so no caller ever chooses based on credential kind.
/// </para>
/// </remarks>
public sealed record BackendCredentialContext
{
    /// <summary>The stored record. Carries the discriminator and the opaque protected blob.</summary>
    public required BackendCredentialRecord Record { get; init; }

    /// <summary>The decrypted credential. Owned by the caller; not to be retained.</summary>
    public required SecretValue Secret { get; init; }

    /// <summary>Which protocol this exchange will be used for.</summary>
    public required BackendProtocol Protocol { get; init; }

    /// <summary>Injected clock. Providers must not read the wall clock (spec §4; Jev §6 uses the same rule).</summary>
    public required TimeProvider TimeProvider { get; init; }
}

/// <summary>
/// Turns a stored credential of one kind into a backend authentication exchange.
/// </summary>
/// <remarks>
/// <b>This is the pluggable seam of spec §9.5.</b> One implementation per credential kind, selected
/// by the discriminator on the stored record, and nothing above it knows or asks which kind it got.
///
/// <para>
/// The migration this is built for: today <c>app-password</c> is registered and <c>oauth-refresh-token</c>
/// ships alongside it as the implementation that takes over when Google winds app passwords down.
/// Because a provider returns the same <see cref="IBackendAuthenticator"/> abstraction and the same
/// <see cref="BackendAuthStyle"/> framing, swapping which one a tenant's records point at is a data
/// change — <b>no caller, no driver and no session changes</b>. That is the test the seam has to
/// pass, and it is asserted directly in the test suite rather than asserted in a comment.
/// </para>
///
/// <para>
/// A provider must never decide <em>policy</em>: it does not get to refuse a session for a reason
/// other than "I cannot produce a credential". It does not choose an action, and it does not widen
/// what the caller may do with the resulting session.
/// </para>
/// </remarks>
public interface IBackendCredentialProvider
{
    /// <summary>The discriminator value this provider claims. Matched ordinally against the record's.</summary>
    string Discriminator { get; }

    /// <summary>
    /// Builds an authenticator for one backend connection.
    /// </summary>
    /// <exception cref="CredentialUnavailableException">The credential cannot be used as stored.</exception>
    ValueTask<IBackendAuthenticator> CreateAuthenticatorAsync(
        BackendCredentialContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// The registered credential providers, indexed by discriminator.
/// </summary>
/// <remarks>
/// <b>Unknown discriminators fail closed.</b> There is no default provider and no "assume app
/// password" fallback. A record whose discriminator this build does not recognise means the data
/// came from a newer deployment, or from a downgrade, or from someone editing the store — and in
/// every one of those cases the safe answer is to refuse the session, not to guess at a credential
/// shape and present it to Google.
/// </remarks>
public sealed class BackendCredentialProviders
{
    private readonly Dictionary<string, IBackendCredentialProvider> _providers = new(StringComparer.Ordinal);

    public BackendCredentialProviders(IEnumerable<IBackendCredentialProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.Discriminator))
            {
                throw new ArgumentException(
                    $"{provider.GetType().Name} declares an empty discriminator.", nameof(providers));
            }

            if (!_providers.TryAdd(provider.Discriminator, provider))
            {
                // Two providers claiming one discriminator is ambiguous, and resolving it by
                // registration order would make which credential kind gets used depend on
                // composition order. Better to fail at wiring time.
                throw new ArgumentException(
                    $"Two credential providers claim the discriminator '{provider.Discriminator}'.",
                    nameof(providers));
            }
        }
    }

    /// <summary>The registered discriminators. Exposed so a deployment can assert its own record set is servable.</summary>
    public IReadOnlyCollection<string> Discriminators => _providers.Keys;

    /// <summary>
    /// The provider for <paramref name="discriminator"/>, or null when none is registered.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception so the caller decides how to report it; see
    /// <see cref="BackendCredentialResolver"/>, which converts it into a fail-closed
    /// <see cref="CredentialUnavailableException"/>.
    /// </remarks>
    public IBackendCredentialProvider? TryGet(string discriminator)
        => _providers.TryGetValue(discriminator, out var provider) ? provider : null;
}
