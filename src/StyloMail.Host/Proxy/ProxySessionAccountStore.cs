using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Hosting;

namespace StyloMail.Host.Proxy;

/// <summary>The connection a protocol session is running for, on its own async flow.</summary>
internal sealed record ProxySessionBinding(
    string SessionId,
    string TenantId,
    ProxyListenerProtocol Protocol);

/// <summary>
/// Ambient binding for the session currently executing.
/// </summary>
/// <remarks>
/// The protocol session resolves the account deep inside access's code, after the client password
/// is verified, and access publishes no account event of its own. An <see cref="AsyncLocal{T}"/>
/// lets the Host's account-store decorator attribute that lookup to the right connection without
/// changing access's signature or sharing mutable state between concurrent sessions.
/// </remarks>
internal static class ProxySessionContext
{
    public static readonly AsyncLocal<ProxySessionBinding?> Current = new();
}

/// <summary>
/// Wraps the account store so a successful login announces the opaque account id for the current
/// session.
/// </summary>
/// <remarks>
/// <para>
/// The connection is opened before authentication, so its first event necessarily carries no
/// account. Publishing a second opened event once the account is known lets the live-session read
/// project the account id without delaying liveness or holding the connection invisible until a
/// password arrives.
/// </para>
/// <para>
/// A missing account announces nothing. A disabled account does announce, because the store returns
/// it deliberately so the authentication boundary can run the decoy verification; the session then
/// rejects and closes, and a closed session is not live in the read model.
/// </para>
/// </remarks>
internal sealed class ProxySessionAccountStore : IStyloMailAccountStore
{
    private readonly IStyloMailAccountStore _inner;
    private readonly Func<string, ValueTask> _onAuthenticated;

    public ProxySessionAccountStore(
        IStyloMailAccountStore inner,
        Func<string, ValueTask> onAuthenticated)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onAuthenticated = onAuthenticated ?? throw new ArgumentNullException(nameof(onAuthenticated));
    }

    public async ValueTask<StyloMailAccount?> FindByLoginAsync(
        string tenantId,
        string login,
        CancellationToken cancellationToken)
    {
        var account = await _inner.FindByLoginAsync(tenantId, login, cancellationToken)
            .ConfigureAwait(false);

        if (account is not null && ProxySessionContext.Current.Value is not null)
        {
            await _onAuthenticated(account.AccountId).ConfigureAwait(false);
        }

        return account;
    }

    public ValueTask<StyloMailAccount?> FindByIdAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken)
        => _inner.FindByIdAsync(tenantId, accountId, cancellationToken);
}
