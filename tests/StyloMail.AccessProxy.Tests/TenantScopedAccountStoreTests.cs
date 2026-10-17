using StyloMail.AccessProxy.Accounts;

namespace StyloMail.AccessProxy.Tests;

public sealed class TenantScopedAccountStoreTests
{
    [Fact]
    public async Task Same_login_in_different_tenants_resolves_to_each_tenants_account()
    {
        var store = new InMemoryStyloMailAccountStore();
        var alpha = Account("tenant-alpha", "account-alpha");
        var beta = Account("tenant-beta", "account-beta");

        store.Add(alpha);
        store.Add(beta);

        var alphaResolved = await store.FindByLoginAsync("tenant-alpha", alpha.Login, CancellationToken.None);
        var betaResolved = await store.FindByLoginAsync("tenant-beta", beta.Login, CancellationToken.None);

        Assert.Equal(alpha.AccountId, alphaResolved?.AccountId);
        Assert.Equal(beta.AccountId, betaResolved?.AccountId);
    }

    [Fact]
    public async Task Login_in_one_tenant_does_not_resolve_in_another_tenant()
    {
        var store = new InMemoryStyloMailAccountStore();
        var alpha = Account("tenant-alpha", "account-alpha");
        store.Add(alpha);

        var positiveControl = await store.FindByLoginAsync("tenant-alpha", alpha.Login, CancellationToken.None);
        var foreignTenant = await store.FindByLoginAsync("tenant-beta", alpha.Login, CancellationToken.None);

        Assert.Equal(alpha.AccountId, positiveControl?.AccountId);
        Assert.Null(foreignTenant);
    }

    private static StyloMailAccount Account(string tenantId, string accountId) => new()
    {
        AccountId = accountId,
        TenantId = tenantId,
        Login = "shared@example.test",
        PasswordHash = new PasswordHash(1, [1], [2]),
        BackendAccountId = accountId,
        Disabled = false,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}
