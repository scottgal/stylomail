using Microsoft.Extensions.Time.Testing;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Imap;
using StyloMail.AccessProxy.Pop3;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Tests.Support;

/// <summary>
/// Wires a complete proxy with every external dependency faked.
/// </summary>
/// <remarks>
/// The stores are the real in-memory implementations and the protector is the real AES-GCM one, so
/// tests that assert "the stored bytes are not the credential" and "a revoked credential never
/// reaches the provider" exercise the production code paths rather than stand-ins that would pass
/// trivially. Only the two genuine boundaries — the provider's socket and its OAuth token service —
/// are faked, and they are faked because the brief forbids the real thing.
/// </remarks>
internal sealed class ProxyHarness
{
    /// <summary>The password the user types into their mail client.</summary>
    public const string ClientPassword = "client-side-password-9f3a";

    /// <summary>A Google-issued app password. Distinct from the client password, deliberately.</summary>
    public const string AppPasswordSecret = "gmail-apppw-4b7c1d9e2f80";

    /// <summary>A Google OAuth refresh token.</summary>
    public const string RefreshTokenSecret = "1//0gRefreshTokenValue-8c2e";

    /// <summary>What the fake token endpoint hands back in exchange.</summary>
    public const string AccessTokenSecret = "ya29.access-token-value-5a1f";

    /// <summary>A second, unrelated secret, to prove one credential's bytes are not another's.</summary>
    public const string OtherSecret = "unrelated-secret-value-77aa";

    public const string TenantId = "tenant-alpha";
    public const string Login = "alice@example.com";

    /// <param name="bounds">
    /// Tightened bounds for the tests that exist to make a limit fire. Left at the production
    /// defaults elsewhere, so those tests exercise the numbers a deployment would actually run with.
    /// </param>
    public ProxyHarness(AccessProxyBounds? bounds = null)
    {
        Clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T09:00:00Z", null));
        KeyRing = InMemorySecretKeyRing.CreateRandom("test-key-1");
        Protector = new AesGcmSecretProtector(KeyRing);
        CredentialStore = new InMemoryBackendCredentialStore(Protector);
        AccountStore = new InMemoryStyloMailAccountStore();
        PasswordHasher = new Pbkdf2StyloMailPasswordHasher(iterations: 1_000);
        OAuth = new FakeOAuthTokenEndpoint();
        Providers = new BackendCredentialProviders(
        [
            new AppPasswordCredentialProvider(),
            new OAuthRefreshTokenCredentialProvider(OAuth),
        ]);
        Resolver = new BackendCredentialResolver(CredentialStore, Protector, Providers, Clock);
        Transport = new FakeBackendTransport();
        Bounds = bounds ?? new AccessProxyBounds();
        Limiter = new SessionLimiter(Bounds);
    }

    public FakeTimeProvider Clock { get; }

    public InMemorySecretKeyRing KeyRing { get; }

    public AesGcmSecretProtector Protector { get; }

    public InMemoryBackendCredentialStore CredentialStore { get; }

    public InMemoryStyloMailAccountStore AccountStore { get; }

    public Pbkdf2StyloMailPasswordHasher PasswordHasher { get; }

    public FakeOAuthTokenEndpoint OAuth { get; }

    public BackendCredentialProviders Providers { get; }

    public BackendCredentialResolver Resolver { get; }

    public FakeBackendTransport Transport { get; }

    public AccessProxyBounds Bounds { get; }

    public SessionLimiter Limiter { get; }

    /// <summary>Registers a client account with a StyloMail-issued password.</summary>
    public StyloMailAccount AddAccount(
        string login = Login,
        string accountId = "acct-1",
        string clientPassword = ClientPassword,
        bool disabled = false)
    {
        var now = Clock.GetUtcNow();
        var account = new StyloMailAccount
        {
            AccountId = accountId,
            TenantId = TenantId,
            Login = login,
            PasswordHash = PasswordHasher.Hash(System.Text.Encoding.UTF8.GetBytes(clientPassword)),
            BackendAccountId = accountId,
            Disabled = disabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        AccountStore.Add(account);
        return account;
    }

    /// <summary>Stores a backend credential of the given kind for an account.</summary>
    public async Task<BackendCredentialRecord> AddBackendCredentialAsync(
        string accountId = "acct-1",
        string discriminator = AppPasswordCredentialProvider.DiscriminatorValue,
        string secret = AppPasswordSecret,
        string backendUsername = Login,
        bool revoked = false)
    {
        using var value = SecretValue.FromUtf8(secret);
        var record = await CredentialStore.StoreAsync(
            new BackendCredentialWrite
            {
                TenantId = TenantId,
                AccountId = accountId,
                ProviderId = "gmail",
                Discriminator = discriminator,
                BackendUsername = backendUsername,
                Secret = value,
                TimeProvider = Clock,
            },
            CancellationToken.None);

        if (revoked)
        {
            await CredentialStore.RevokeAsync(accountId, CancellationToken.None);
            record = (await CredentialStore.FindAsync(accountId, CancellationToken.None))!;
        }

        return record;
    }

    /// <summary>An account with a working StyloMail password and an app-password backend credential.</summary>
    public async Task EnrolAppPasswordAccountAsync()
    {
        AddAccount();
        await AddBackendCredentialAsync();
    }

    /// <summary>An account with a working StyloMail password and an OAuth backend credential.</summary>
    public async Task EnrolOAuthAccountAsync()
    {
        AddAccount();
        await AddBackendCredentialAsync(
            discriminator: OAuthRefreshTokenCredentialProvider.DiscriminatorValue,
            secret: RefreshTokenSecret);
    }

    public ImapAccessProxySession NewImapSession(IRetrievalObserver? observer = null) =>
        new(AccountStore, PasswordHasher, Resolver, NewImapConnector(), Limiter, Bounds, Clock, observer);

    public Pop3AccessProxySession NewPop3Session(IRetrievalObserver? observer = null) =>
        new(AccountStore, PasswordHasher, Resolver, NewPop3Connector(), Limiter, Bounds, Clock, observer);

    public ImapBackendConnector NewImapConnector() =>
        new(Transport, Resolver, Bounds);

    public Pop3BackendConnector NewPop3Connector() =>
        new(Transport, Resolver, Bounds);
}
