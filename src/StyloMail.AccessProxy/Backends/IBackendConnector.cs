using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>What a backend connection needs to know.</summary>
public sealed record BackendConnectionRequest
{
    /// <summary>The StyloMail account whose backend credential should be used.</summary>
    public required string AccountId { get; init; }

    public required BackendProtocol Protocol { get; init; }

    /// <summary>Injected clock, for connect and authentication timeouts.</summary>
    public required TimeProvider TimeProvider { get; init; }
}

/// <summary>
/// Opens an authenticated session to a mail provider.
/// </summary>
/// <remarks>
/// One method, and its postcondition is the important part: <b>when it returns, the channel is
/// authenticated and ready for relay</b>. The caller never sees a half-open connection, never sees
/// the backend's greeting, and never performs any part of the authentication itself.
///
/// <para>
/// That is what keeps the credential seam invisible to the session. The session asks for "a channel
/// to this account's backend"; the connector resolves the credential, drives whatever exchange the
/// credential kind requires, and hands back bytes. A driver that had to authenticate would have to
/// know whether it was sending a password or a bearer token — and that is exactly the branch spec
/// §9.5 forbids above the seam.
/// </para>
///
/// <para>
/// <b>Failure is fail-closed and terminal.</b> A connector either returns an authenticated channel
/// or throws. It does not retry the authentication exchange, and it does not return an
/// unauthenticated channel for the caller to repair — a proxy that relays as an unauthenticated
/// party is worse than one that refuses.
/// </para>
/// </remarks>
public interface IBackendConnector
{
    /// <summary>
    /// Diagnostic label for the provider this connector reaches, e.g. <c>gmail</c>.
    /// </summary>
    /// <remarks>
    /// Owned by the connector rather than passed in, because it is a property of the connection
    /// being made and not of the session making it — and because a label supplied by a caller is a
    /// label that can be got wrong. Never message content and never a credential.
    /// </remarks>
    string ProviderLabel { get; }

    /// <summary>
    /// Opens and authenticates a backend channel.
    /// </summary>
    /// <exception cref="CredentialUnavailableException">
    /// The credential could not be produced — revoked, missing, unknown kind, undecryptable.
    /// </exception>
    /// <exception cref="BackendAuthenticationRejectedException">
    /// The credential was produced and the provider refused it.
    /// </exception>
    ValueTask<IDuplexChannel> ConnectAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken);
}
