namespace StyloMail.AccessProxy.Credentials;

/// <summary>
/// How a backend authentication exchange is framed on the wire.
/// </summary>
/// <remarks>
/// <b>This enum describes the wire, not the credential.</b> It is the one thing the protocol drivers
/// are allowed to switch on, and it is deliberately orthogonal to which kind of credential is being
/// used: today the app-password provider speaks <see cref="Sasl"/> with mechanism <c>PLAIN</c> and
/// the OAuth provider speaks <see cref="Sasl"/> with mechanism <c>XOAUTH2</c>. Both are the same
/// style, so the OAuth swap changes no driver code at all.
///
/// <para>
/// <see cref="LegacyUsernamePassword"/> exists because POP3's classical authentication is two bare
/// commands (<c>USER</c> / <c>PASS</c>) rather than a SASL exchange, and pretending otherwise would
/// have pushed a credential-kind check up into the POP3 driver, exactly the branch spec §9.5
/// forbids. Modelling the framing explicitly keeps that decision below the seam where it belongs.
/// A future credential kind that reuses either style needs no driver change; only a genuinely new
/// wire style would, and that is a protocol fact, not a credential fact.
/// </para>
/// </remarks>
public enum BackendAuthStyle
{
    /// <summary>SASL: an <c>AUTHENTICATE</c>/<c>AUTH</c> command and base64 challenge/response turns.</summary>
    Sasl = 0,

    /// <summary>A sequence of bare credential tokens, each sent as its own protocol verb (POP3 <c>USER</c>/<c>PASS</c>).</summary>
    LegacyUsernamePassword = 1,
}

/// <summary>
/// One backend authentication exchange, as a sequence of opaque tokens.
/// </summary>
/// <remarks>
/// <b>This interface is the seam.</b> Spec §9.5: define a credential-provider seam with two
/// implementations so the OAuth migration is an implementation swap, not a redesign, and nothing
/// above the seam may branch on which kind of credential it is.
///
/// <para>
/// The way that is enforced structurally is by never handing anything credential-shaped upwards.
/// A driver does not receive a password, a token, a username, or a mechanism-specific object. It
/// receives this: a source of opaque tokens, plus a <see cref="BackendAuthStyle"/> telling it how
/// to frame them. The driver relays bytes and never learns what they mean.
/// </para>
///
/// <para>
/// <b>Token semantics.</b> <see cref="NextAsync"/> is called with a null challenge to obtain the
/// exchange's opening token, and with the server's challenge bytes thereafter. Returning null means
/// <em>abandon the exchange</em>: the provider has decided that continuing is wrong, an OAuth error
/// challenge, a credential that turned out to be unusable, and the driver must fail the session
/// closed rather than prompt again. That null return is how spec §9.5 risk 4 ("fail closed, never a
/// silent retry loop") is expressed without the driver knowing anything about credentials.
/// </para>
///
/// <para>
/// Implementations are single-use and single-session: one authenticator authenticates one backend
/// connection, and must not be replayed onto a second. Reuse would let a provider that rotates a
/// token cache a stale response across sessions.
/// </para>
///
/// <para>
/// <b>Dispose is part of the contract.</b> An authenticator holds decrypted credential bytes for the
/// duration of the exchange; disposing zeroes them. The driver disposes in a finally as soon as the
/// backend has answered, so the window in which a plaintext credential sits in managed memory is
/// the length of the authentication exchange rather than the length of the mail session, which can
/// be hours. Given spec §9.4 item 3 (a refresh token is a bearer credential for the whole mailbox),
/// shrinking that window is worth one interface method.
/// </para>
/// </remarks>
public interface IBackendAuthenticator : IDisposable
{
    /// <summary>How the driver frames <see cref="NextAsync"/>'s tokens on the wire.</summary>
    BackendAuthStyle Style { get; }

    /// <summary>
    /// The SASL mechanism name, required when <see cref="Style"/> is <see cref="BackendAuthStyle.Sasl"/>
    /// and ignored otherwise.
    /// </summary>
    string? SaslMechanism { get; }

    /// <summary>
    /// Produces the next token to send.
    /// </summary>
    /// <param name="serverChallenge">
    /// Null to obtain the opening token; otherwise the server's challenge bytes, already base64-decoded.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The token to send, or null to abandon the exchange and fail closed.</returns>
    ValueTask<SecretValue?> NextAsync(ReadOnlyMemory<byte>? serverChallenge, CancellationToken cancellationToken);
}

/// <summary>
/// The backend rejected the credential we presented.
/// </summary>
/// <remarks>
/// Distinguished from <see cref="CredentialUnavailableException"/>, "we could not produce a
/// credential", because the two have different operator actions. This one means the credential was
/// produced and the provider refused it, which for an app password usually means it was revoked or
/// mistyped on the Google side, and for OAuth usually means the refresh token was revoked.
///
/// <para>
/// <b>It is never retried.</b> A rejected credential presented again is a failed login attempt as
/// far as Google is concerned; a proxy that retries on behalf of every connected client is a
/// credential-stuffing pattern aimed at the user's own mailbox. The client is told authentication
/// failed and the session ends.
/// </para>
/// </remarks>
public sealed class BackendAuthenticationRejectedException : Exception
{
    public BackendAuthenticationRejectedException(string message)
        : base(message)
    {
    }

    public BackendAuthenticationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
