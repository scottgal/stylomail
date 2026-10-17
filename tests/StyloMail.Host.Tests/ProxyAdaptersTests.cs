using System.Text;
using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Credentials;
using StyloMail.Host.Proxy;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// The durable proxy adapters: login-key agreement, tenant isolation, decoy-eligible disabled
/// accounts, and a revocation that is authoritative and never mistaken for a missing credential.
/// </summary>
/// <remarks>
/// The keys below are fixtures, not secrets: they exist only inside this process and protect a
/// database that is deleted when the test ends.
/// </remarks>
public sealed class ProxyAdaptersTests : IDisposable
{
    private static readonly byte[] LoginKey = Encoding.UTF8.GetBytes("fixture-login-key-32-bytes-long!!");
    private static readonly byte[] ProtectionKey = Encoding.UTF8.GetBytes("fixture-protection-key-32-bytes!");
    private static readonly string[] ExpectedAccountIds = ["acct-a", "acct-b"];

    private readonly string _path;
    private readonly HostDatabase _database;
    private readonly IProxyLoginKeyDeriver _deriver;
    private readonly SqliteProxyEnrollmentPersistence _persistence;
    private readonly IStyloMailPasswordHasher _hasher;
    private readonly ISecretProtector _protector;
    private readonly ProxyEnrollmentService _enrollment;
    private readonly SqliteProxyAccountStore _accounts;
    private readonly SqliteBackendCredentialStore _credentials;

    public ProxyAdaptersTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"stylomail-proxy-adapters-{Guid.NewGuid():N}.db");
        _database = new HostDatabase(Options.Create(new HostStorageOptions { DatabasePath = _path }));
        _database.EnsureCreated();

        _deriver = new HmacProxyLoginKeyDeriver(LoginKey);
        _persistence = new SqliteProxyEnrollmentPersistence(_database);
        _hasher = new Pbkdf2StyloMailPasswordHasher(1_000);
        _protector = new AesGcmSecretProtector(new InMemorySecretKeyRing("fixture-1", ProtectionKey));
        _enrollment = new ProxyEnrollmentService(_persistence, _hasher, _protector, _deriver, TimeProvider.System);
        _accounts = new SqliteProxyAccountStore(_database, _deriver);
        _credentials = new SqliteBackendCredentialStore(_database, _protector, TimeProvider.System);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leaked handle must not turn a green suite red; the temp file is disposable.
        }
    }

    [Fact]
    public async Task Enrolment_and_authentication_derive_the_same_login_key()
    {
        await EnrolAsync("acct-1", " Alice@Example.com ", "backend-1");

        // Different case and surrounding whitespace are the same login, because enrolment normalized
        // them and lookup must apply exactly the same rule.
        var found = await _accounts.FindByLoginAsync("acme", "alice@example.com", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("acct-1", found!.AccountId);
        Assert.Equal("backend-1", found.BackendAccountId);
        Assert.False(found.Disabled);
    }

    [Fact]
    public async Task Login_lookup_is_tenant_scoped()
    {
        await EnrolAsync("acct-1", "alice@example.com", "backend-1");

        var otherTenant = await _accounts.FindByLoginAsync("globex", "alice@example.com", CancellationToken.None);

        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task A_disabled_account_still_resolves_and_is_flagged_for_the_decoy_path()
    {
        // The authentication boundary must run the same decoy PBKDF2 for a missing account and a
        // disabled one. If storage filtered the disabled row out, the two paths would separate again
        // and the store would be deciding an authentication property it cannot see the timing of.
        await EnrolAsync("acct-1", "alice@example.com", "backend-1");
        await _persistence.SetAccountEnabledAsync(
            new ProxyAccountStateChange
            {
                TenantId = "acme",
                AccountId = "acct-1",
                Enabled = false,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var found = await _accounts.FindByLoginAsync("acme", "alice@example.com", CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found!.Disabled);
    }

    [Fact]
    public async Task Credentials_are_tenant_scoped_and_revocation_is_authoritative()
    {
        await EnrolAsync("acct-1", "alice@example.com", "backend-1");

        var active = await _credentials.FindAsync("acme", "backend-1", CancellationToken.None);
        Assert.NotNull(active);
        Assert.False(active!.Revoked);

        Assert.True(await _credentials.RevokeAsync("acme", "backend-1", CancellationToken.None));

        // Still returned, now explicitly revoked: "revoked" and "never enrolled" are different
        // answers and only one of them should invite a caller to enroll it again.
        var revoked = await _credentials.FindAsync("acme", "backend-1", CancellationToken.None);
        Assert.NotNull(revoked);
        Assert.True(revoked!.Revoked);

        Assert.Null(await _credentials.FindAsync("globex", "backend-1", CancellationToken.None));
    }

    [Fact]
    public async Task Re_enrolment_replaces_the_ciphertext_and_clears_revocation()
    {
        await EnrolAsync("acct-1", "alice@example.com", "backend-1");
        await _credentials.RevokeAsync("acme", "backend-1", CancellationToken.None);

        using var secret = SecretValue.FromUtf8("a-new-backend-secret");
        var stored = await _credentials.StoreAsync(
            new BackendCredentialWrite
            {
                TenantId = "acme",
                AccountId = "backend-1",
                ProviderId = "gmail",
                Discriminator = "app-password",
                BackendUsername = "backend-user",
                Secret = secret,
                TimeProvider = TimeProvider.System,
            },
            CancellationToken.None);

        Assert.False(stored.Revoked);
        var readBack = await _credentials.FindAsync("acme", "backend-1", CancellationToken.None);
        Assert.NotNull(readBack);
        Assert.False(readBack!.Revoked);
    }

    [Fact]
    public async Task The_stored_credential_bytes_are_not_the_plaintext()
    {
        await EnrolAsync("acct-1", "alice@example.com", "backend-1", backendSecret: "backend-secret-plaintext");

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT protected_secret FROM proxy_backend_credential WHERE tenant_id='acme' AND backend_account_id='backend-1';";
        var blob = (byte[])command.ExecuteScalar()!;

        var plaintext = Encoding.UTF8.GetBytes("backend-secret-plaintext");
        Assert.True(blob.AsSpan().IndexOf(plaintext) < 0);
    }

    [Fact]
    public async Task Account_listing_is_ordered_and_joins_the_credential_projection()
    {
        await EnrolAsync("acct-b", "b@example.com", "backend-b");
        await EnrolAsync("acct-a", "a@example.com", "backend-a");

        var page = await _accounts.ListAsync("acme", 10, null, CancellationToken.None);

        Assert.Equal(ExpectedAccountIds, page.Accounts.Select(account => account.AccountId));
        Assert.All(page.Accounts, account =>
        {
            Assert.Equal("gmail", account.ProviderId);
            Assert.Equal("app-password", account.CredentialDiscriminator);
            Assert.Equal("active", account.EnrollmentState);
            Assert.DoesNotContain("@", account.LoginKey);
        });
    }

    private Task EnrolAsync(
        string accountId,
        string login,
        string backendAccountId,
        string backendSecret = "backend-secret")
    {
        using var clientPassword = SecretValue.FromUtf8("client-password");
        using var secret = SecretValue.FromUtf8(backendSecret);

        return _enrollment.EnrolAsync(
            new ProxyEnrollmentRequest
            {
                TenantId = "acme",
                AccountId = accountId,
                Login = login,
                ClientPassword = clientPassword,
                BackendAccountId = backendAccountId,
                ProviderId = "gmail",
                Discriminator = "app-password",
                BackendUsername = "backend-user",
                BackendSecret = secret,
            },
            CancellationToken.None).AsTask();
    }
}
