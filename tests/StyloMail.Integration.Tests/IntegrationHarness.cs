using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Imap;
using StyloMail.AccessProxy.Pop3;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// Wires a proxy session against a real backend, using the public surface of
/// <c>StyloMail.AccessProxy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than reusing <c>ProxyHarness</c>.</b> The plan asked this project to
/// reference <c>StyloMail.AccessProxy.Tests</c> and reuse its harness. That does not work:
/// <c>ProxyHarness</c> is <c>internal</c> and that project grants no <c>InternalsVisibleTo</c>, so the
/// reference resolves no types at all. Every type needed here is <c>public</c> on
/// <c>StyloMail.AccessProxy</c> itself, so this wires the same shape directly and the reference
/// becomes unnecessary rather than relied upon.
/// </para>
/// <para>
/// <b>Real time, not a fake clock.</b> <c>ProxyHarness</c> injects a fake time provider so the unit
/// suite can drive a timeout without waiting. These tests are already paying for real sockets and a
/// container, and a fake clock would not advance on its own, so the wall clock is the honest choice.
/// The bounds are the production defaults: the idle bound is half an hour, far beyond any test.
/// </para>
/// </remarks>
internal sealed class IntegrationHarness
{
    /// <summary>The login the mail client presents to StyloMail, which StyloMail issues.</summary>
    internal const string ClientLogin = "alice@example.com";

    /// <summary>The password the mail client presents to StyloMail. Not the backend credential.</summary>
    internal const string ClientPassword = "client-side-password-9f3a";

    internal IntegrationHarness(IBackendTransport backend)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Clock = TimeProvider.System;
        KeyRing = InMemorySecretKeyRing.CreateRandom("integration-key-1");
        Protector = new AesGcmSecretProtector(KeyRing);
        CredentialStore = new InMemoryBackendCredentialStore(Protector);
        AccountStore = new InMemoryStyloMailAccountStore();
        PasswordHasher = new Pbkdf2StyloMailPasswordHasher(iterations: 1_000);
        Providers = new BackendCredentialProviders([new AppPasswordCredentialProvider()]);
        Resolver = new BackendCredentialResolver(CredentialStore, Protector, Providers, Clock);
        Bounds = new AccessProxyBounds();
        Limiter = new SessionLimiter(Bounds);
    }

    internal TimeProvider Clock { get; }

    internal InMemorySecretKeyRing KeyRing { get; }

    internal AesGcmSecretProtector Protector { get; }

    internal InMemoryBackendCredentialStore CredentialStore { get; }

    internal InMemoryStyloMailAccountStore AccountStore { get; }

    internal Pbkdf2StyloMailPasswordHasher PasswordHasher { get; }

    internal BackendCredentialProviders Providers { get; }

    internal BackendCredentialResolver Resolver { get; }

    internal AccessProxyBounds Bounds { get; }

    internal SessionLimiter Limiter { get; }

    internal IBackendTransport Backend { get; }

    /// <summary>
    /// Enrols a client account whose backend credential is the one the container was built with.
    /// </summary>
    /// <remarks>
    /// The two secrets are deliberately different values with different roles. The client presents
    /// <see cref="ClientPassword"/> to StyloMail and never learns the backend password; StyloMail
    /// resolves the backend password from its own store and presents it to the container. If the
    /// proxy ever forwarded the client's password to the backend instead, the container would reject
    /// it and the test would fail, which is exactly the confusion worth catching.
    /// </remarks>
    internal async Task EnrolAsync(string backendUsername, string backendSecret)
    {
        var now = Clock.GetUtcNow();

        AccountStore.Add(new StyloMailAccount
        {
            AccountId = "acct-1",
            TenantId = "tenant-alpha",
            Login = ClientLogin,
            PasswordHash = PasswordHasher.Hash(Encoding.UTF8.GetBytes(ClientPassword)),
            BackendAccountId = "acct-1",
            Disabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        });

        using var secret = SecretValue.FromUtf8(backendSecret);
        await CredentialStore.StoreAsync(
            new BackendCredentialWrite
            {
                TenantId = "tenant-alpha",
                AccountId = "acct-1",
                ProviderId = "greenmail",
                Discriminator = AppPasswordCredentialProvider.DiscriminatorValue,
                BackendUsername = backendUsername,
                Secret = secret,
                TimeProvider = Clock,
            },
            CancellationToken.None);
    }

    /// <summary>A session whose backend connections go to the real server.</summary>
    internal ImapAccessProxySession NewImapSession()
        => new(AccountStore, PasswordHasher, Resolver, NewImapConnector(), Limiter, Bounds, Clock);

    internal Pop3AccessProxySession NewPop3Session()
        => new(AccountStore, PasswordHasher, Resolver, NewPop3Connector(), Limiter, Bounds, Clock);

    internal ImapBackendConnector NewImapConnector() => new(Backend, Resolver, Bounds);

    internal Pop3BackendConnector NewPop3Connector() => new(Backend, Resolver, Bounds);
}
