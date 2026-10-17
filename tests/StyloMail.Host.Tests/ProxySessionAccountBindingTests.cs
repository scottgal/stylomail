using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Hosting;
using StyloMail.Host.Proxy;

namespace StyloMail.Host.Tests;

/// <summary>
/// The bridge that puts an authenticated account id on a live session.
/// </summary>
/// <remarks>
/// The protocol session resolves the account inside access's code and returns only a
/// <c>SessionOutcome</c>. The account-store decorator announces the lookup on the ambient session
/// binding, and the Host publishes a follow-up opened event. These tests pin the attribution and
/// the "unknown announces nothing" rule.
/// </remarks>
public sealed class ProxySessionAccountBindingTests
{
    [Fact]
    public async Task A_successful_login_announces_the_opaque_account_for_the_current_session()
    {
        string? announced = null;
        var store = new ProxySessionAccountStore(
            new StubAccountStore(AccessAccount("pa_1")),
            accountId =>
            {
                announced = accountId;
                return ValueTask.CompletedTask;
            });

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_1", "acme", ProxyListenerProtocol.Imap);
        try
        {
            var found = await store.FindByLoginAsync("acme", "alice@example.com", CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal("pa_1", announced);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task An_unknown_login_announces_nothing()
    {
        var announced = 0;
        var store = new ProxySessionAccountStore(
            new StubAccountStore(null),
            _ =>
            {
                announced++;
                return ValueTask.CompletedTask;
            });

        ProxySessionContext.Current.Value =
            new ProxySessionBinding("ps_1", "acme", ProxyListenerProtocol.Imap);
        try
        {
            Assert.Null(await store.FindByLoginAsync(
                "acme", "nobody@example.com", CancellationToken.None));
            Assert.Equal(0, announced);
        }
        finally
        {
            ProxySessionContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task A_lookup_outside_a_session_publishes_nothing()
    {
        var announced = 0;
        var store = new ProxySessionAccountStore(
            new StubAccountStore(AccessAccount("pa_1")),
            _ =>
            {
                announced++;
                return ValueTask.CompletedTask;
            });

        ProxySessionContext.Current.Value = null;

        Assert.NotNull(await store.FindByLoginAsync("acme", "alice@example.com", CancellationToken.None));
        Assert.Equal(0, announced);
    }

    private static StyloMailAccount AccessAccount(string accountId) => new()
    {
        AccountId = accountId,
        TenantId = "acme",
        Login = "pseudonym",
        PasswordHash = new PasswordHash(1, [1], [1]),
        BackendAccountId = "backend-1",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class StubAccountStore : IStyloMailAccountStore
    {
        private readonly StyloMailAccount? _account;

        public StubAccountStore(StyloMailAccount? account) => _account = account;

        public ValueTask<StyloMailAccount?> FindByLoginAsync(
            string tenantId,
            string login,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(_account);

        public ValueTask<StyloMailAccount?> FindByIdAsync(
            string tenantId,
            string accountId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(_account);
    }
}
