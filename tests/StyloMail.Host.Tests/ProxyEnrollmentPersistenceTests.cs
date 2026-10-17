using Microsoft.Extensions.Options;
using StyloMail.AccessProxy.Accounts;
using StyloMail.Host.Storage;

namespace StyloMail.Host.Tests;

/// <summary>
/// The durable proxy enrolment store: one transaction per operation, typed rejections, and a
/// revoked credential that fails closed rather than being retried.
/// </summary>
/// <remarks>
/// <para>
/// Every rejection assertion names the <see cref="ProxyEnrollmentConflict"/> member rather than the
/// exception type. An exception type alone cannot tell a duplicate login from a tenant mismatch, so
/// a test that asserted only the type would pass while the route layer displayed the wrong reason.
/// </para>
/// <para>
/// The store is the only place this contract is enforced, so these tests read columns back rather
/// than trusting the return value of the call that wrote them.
/// </para>
/// </remarks>
public sealed class ProxyEnrollmentPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private readonly string _path;
    private readonly HostDatabase _database;
    private readonly SqliteProxyEnrollmentPersistence _store;

    public ProxyEnrollmentPersistenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"stylomail-proxy-store-{Guid.NewGuid():N}.db");
        _database = new HostDatabase(Options.Create(new HostStorageOptions { DatabasePath = _path }));
        _database.EnsureCreated();
        _store = new SqliteProxyEnrollmentPersistence(_database);
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
    public async Task Enrol_writes_both_rows_in_one_transaction()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM proxy_account WHERE account_id='acct-1';"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM proxy_backend_credential WHERE backend_account_id='backend-1';"));
    }

    [Fact]
    public async Task Enrol_duplicate_login_key_in_same_tenant_throws_DuplicateLoginKey()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "same-login", "backend-1"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.EnrolAsync(Enrollment("acct-2", "same-login", "backend-2"), CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.DuplicateLoginKey, conflict.Conflict);
    }

    [Fact]
    public async Task Enrol_duplicate_account_id_throws_DuplicateAccountId()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.EnrolAsync(Enrollment("acct-1", "login-2", "backend-2"), CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.DuplicateAccountId, conflict.Conflict);
    }

    [Fact]
    public async Task Enrol_tenant_mismatch_throws_TenantMismatch()
    {
        var enrollment = Enrollment("acct-1", "login-1", "backend-1") with
        {
            Credential = Enrollment("acct-1", "login-1", "backend-1").Credential with { TenantId = "beta" },
        };

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.EnrolAsync(enrollment, CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.TenantMismatch, conflict.Conflict);
    }

    [Fact]
    public async Task Enrol_reusing_a_backend_credential_throws_BackendCredentialUnavailable()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.EnrolAsync(Enrollment("acct-2", "login-2", "backend-1"), CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.BackendCredentialUnavailable, conflict.Conflict);
    }

    [Fact]
    public async Task SetAccountEnabled_disables_and_enables_the_row()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1", enabled: true), CancellationToken.None);

        await _store.SetAccountEnabledAsync(
            new ProxyAccountStateChange { TenantId = "alpha", AccountId = "acct-1", Enabled = false, UpdatedAt = At.AddMinutes(1) },
            CancellationToken.None);
        Assert.Equal(1L, Scalar("SELECT disabled FROM proxy_account WHERE account_id='acct-1';"));

        await _store.SetAccountEnabledAsync(
            new ProxyAccountStateChange { TenantId = "alpha", AccountId = "acct-1", Enabled = true, UpdatedAt = At.AddMinutes(2) },
            CancellationToken.None);
        Assert.Equal(0L, Scalar("SELECT disabled FROM proxy_account WHERE account_id='acct-1';"));
    }

    [Fact]
    public async Task SetAccountEnabled_unknown_account_throws_AccountNotFound()
    {
        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.SetAccountEnabledAsync(
                new ProxyAccountStateChange { TenantId = "alpha", AccountId = "missing", Enabled = false, UpdatedAt = At },
                CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.AccountNotFound, conflict.Conflict);
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_keeps_the_first_revocation_instant()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        await _store.RevokeBackendCredentialAsync(
            new ProxyBackendCredentialRevocation { TenantId = "alpha", BackendAccountId = "backend-1", RevokedAt = At },
            CancellationToken.None);
        await _store.RevokeBackendCredentialAsync(
            new ProxyBackendCredentialRevocation { TenantId = "alpha", BackendAccountId = "backend-1", RevokedAt = At.AddHours(1) },
            CancellationToken.None);

        var revokedAt = (string)ScalarObject("SELECT revoked_at FROM proxy_backend_credential WHERE backend_account_id='backend-1';")!;
        Assert.Equal(At.ToString("O"), revokedAt);
    }

    [Fact]
    public async Task Revoke_unknown_credential_throws_CredentialNotFound()
    {
        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.RevokeBackendCredentialAsync(
                new ProxyBackendCredentialRevocation { TenantId = "alpha", BackendAccountId = "missing", RevokedAt = At },
                CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.CredentialNotFound, conflict.Conflict);
    }

    [Fact]
    public async Task Rewrap_updates_ciphertext_and_key_id_only()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        await _store.RewrapBackendCredentialAsync(
            new ProxyBackendCredentialRewrap
            {
                TenantId = "alpha",
                BackendAccountId = "backend-1",
                ProtectedSecret = [9, 9, 9],
                ProtectionKeyId = "key-2",
                UpdatedAt = At.AddMinutes(5),
            },
            CancellationToken.None);

        Assert.Equal("key-2", (string)ScalarObject("SELECT protection_key_id FROM proxy_backend_credential WHERE backend_account_id='backend-1';")!);
        Assert.Equal(3L, Scalar("SELECT length(protected_secret) FROM proxy_backend_credential WHERE backend_account_id='backend-1';"));
    }

    [Fact]
    public async Task Rewrap_revoked_credential_throws_BackendCredentialUnavailable()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);
        await _store.RevokeBackendCredentialAsync(
            new ProxyBackendCredentialRevocation { TenantId = "alpha", BackendAccountId = "backend-1", RevokedAt = At },
            CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.RewrapBackendCredentialAsync(
                new ProxyBackendCredentialRewrap
                {
                    TenantId = "alpha",
                    BackendAccountId = "backend-1",
                    ProtectedSecret = [1, 2, 3],
                    ProtectionKeyId = "key-2",
                    UpdatedAt = At.AddMinutes(5),
                },
                CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.BackendCredentialUnavailable, conflict.Conflict);
    }

    [Fact]
    public async Task Reenrol_replaces_the_pair_and_clears_revocation()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);
        await _store.RevokeBackendCredentialAsync(
            new ProxyBackendCredentialRevocation { TenantId = "alpha", BackendAccountId = "backend-1", RevokedAt = At },
            CancellationToken.None);

        var replacement = Enrollment("acct-1", "login-1", "backend-1") with
        {
            Account = Enrollment("acct-1", "login-1", "backend-1").Account with
            {
                PasswordHash = new PasswordHash(210_000, [7, 7], [8, 8]),
                UpdatedAt = At.AddMinutes(10),
            },
            Credential = Enrollment("acct-1", "login-1", "backend-1").Credential with
            {
                ProtectedSecret = [4, 4, 4],
                ProtectionKeyId = "key-3",
                UpdatedAt = At.AddMinutes(10),
            },
        };

        await _store.ReenrolAsync(replacement, CancellationToken.None);

        Assert.Equal(210_000L, Scalar("SELECT password_hash_iterations FROM proxy_account WHERE account_id='acct-1';"));
        Assert.IsType<DBNull>(ScalarObject("SELECT revoked_at FROM proxy_backend_credential WHERE backend_account_id='backend-1';"));
        Assert.Equal("key-3", (string)ScalarObject("SELECT protection_key_id FROM proxy_backend_credential WHERE backend_account_id='backend-1';")!);
    }

    [Fact]
    public async Task Reenrol_missing_account_throws_AccountNotFound()
    {
        await _store.EnrolAsync(Enrollment("acct-1", "login-1", "backend-1"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProxyEnrollmentConflictException>(async () =>
            await _store.ReenrolAsync(Enrollment("acct-missing", "login-1", "backend-1"), CancellationToken.None));

        Assert.Equal(ProxyEnrollmentConflict.AccountNotFound, conflict.Conflict);
    }

    private static ProxyEnrollment Enrollment(
        string accountId,
        string loginKey,
        string backendAccountId,
        bool enabled = true)
        => new()
        {
            Account = new ProxyAccountEnrollment
            {
                TenantId = "alpha",
                AccountId = accountId,
                LoginKey = loginKey,
                PasswordHash = new PasswordHash(120_000, [1, 2, 3], [4, 5, 6]),
                BackendAccountId = backendAccountId,
                Enabled = enabled,
                CreatedAt = At,
                UpdatedAt = At,
            },
            Credential = new ProxyBackendCredentialEnrollment
            {
                TenantId = "alpha",
                BackendAccountId = backendAccountId,
                ProviderId = "gmail",
                Discriminator = "app-password",
                ProtectedSecret = [1, 1, 1],
                ProtectionKeyId = "key-1",
                BackendUsername = "alice@example.com",
                CreatedAt = At,
                UpdatedAt = At,
            },
        };

    private long Scalar(string sql) => Convert.ToInt64(ScalarObject(sql));

    private object? ScalarObject(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
