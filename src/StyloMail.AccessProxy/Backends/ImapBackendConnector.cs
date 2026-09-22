using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>
/// Authenticates an IMAP channel to the provider.
/// </summary>
/// <remarks>
/// The framing half of spec §9.5's seam. It reads <see cref="IBackendAuthenticator.Style"/> and
/// frames opaque tokens accordingly, <c>AUTHENTICATE</c> for <see cref="BackendAuthStyle.Sasl"/>,
/// <c>LOGIN</c> for <see cref="BackendAuthStyle.LegacyUsernamePassword"/>, and never learns what
/// either token means. An app password arriving as SASL <c>PLAIN</c> and an OAuth access token
/// arriving as SASL <c>XOAUTH2</c> take the identical path through this class; only the mechanism
/// name the provider chose differs, and that string is never interpreted here.
///
/// <para>
/// Every negative outcome below is terminal. There is no retry, no fallback mechanism, and no
/// "try the other style", a proxy that reattempts a rejected credential is a proxy generating
/// failed login attempts against the user's own mailbox.
/// </para>
/// </remarks>
public sealed class ImapBackendConnector : BackendConnectorBase
{
    private const string Tag = "S1";

    public ImapBackendConnector(
        IBackendTransport transport,
        BackendCredentialResolver credentials,
        AccessProxyBounds bounds)
        : base(transport, credentials, bounds)
    {
    }

    public override string ProviderLabel => "imap-backend";

    protected override BackendProtocol Protocol => BackendProtocol.Imap;

    private protected override async ValueTask AuthenticateAsync(
        IDuplexChannel channel,
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var greeting = await reader
            .ReadLineAsync(Bounds.BackendAuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BackendAuthenticationRejectedException(
                "The IMAP backend closed the connection without sending a greeting.");

        // "* PREAUTH" means the backend already considers this channel authenticated.
        if (greeting.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase))
        {
            // Includes "* BYE", and includes anything unrecognised, an unexpected banner fails
            // closed rather than being read as consent.
            throw new BackendAuthenticationRejectedException("The IMAP backend refused the connection.");
        }

        if (authenticator.Style == BackendAuthStyle.Sasl)
        {
            await AuthenticateSaslAsync(reader, writer, authenticator, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AuthenticateLegacyAsync(reader, writer, authenticator, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AuthenticateSaslAsync(
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var mechanism = authenticator.SaslMechanism
            ?? throw new CredentialUnavailableException(
                "The credential provider declared SASL framing but named no mechanism.");

        var opening = await authenticator.NextAsync(null, cancellationToken).ConfigureAwait(false);

        if (opening is null)
        {
            // No initial response: the backend must issue a challenge first.
            await writer.WriteLineAsync($"{Tag} AUTHENTICATE {mechanism}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using (opening)
            {
                await writer
                    .WriteRawLineAsync(
                        WireCommands.Concatenate($"{Tag} AUTHENTICATE {mechanism} ", WireCommands.Base64(opening)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        for (var round = 1; ; round++)
        {
            CheckRoundBudget(round);

            var reply = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);

            if (reply.StartsWith('+'))
            {
                var challengeText = reply.Length > 1 && reply[1] == ' ' ? reply[2..] : reply[1..];
                var challenge = WireCommands.DecodeBase64(challengeText, "SASL challenge");

                using var response = await authenticator
                    .NextAsync(challenge, cancellationToken)
                    .ConfigureAwait(false);

                if (response is null)
                {
                    // The provider declined to continue, a revoked token, an error challenge. Fail
                    // closed rather than prompting again.
                    throw new BackendAuthenticationRejectedException(
                        "The credential provider abandoned the authentication exchange.");
                }

                await writer
                    .WriteRawLineAsync(WireCommands.Base64(response), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (IsTagged(reply, "OK"))
            {
                return;
            }

            if (IsTagged(reply, "NO") || IsTagged(reply, "BAD"))
            {
                // The backend's own status text is deliberately not echoed: it is untrusted and has
                // been known to quote the credential back.
                throw new BackendAuthenticationRejectedException("The IMAP backend rejected the credential.");
            }

            throw new AccessProxyProtocolException("The IMAP backend sent an unexpected authentication reply.");
        }
    }

    private async ValueTask AuthenticateLegacyAsync(
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        using var user = await authenticator.NextAsync(null, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialUnavailableException("The credential provider produced no username.");
        using var password = await authenticator.NextAsync(null, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialUnavailableException("The credential provider produced no password.");

        await writer
            .WriteRawLineAsync(WireCommands.ImapLogin(Tag, user.Utf8, password.Utf8), cancellationToken)
            .ConfigureAwait(false);

        var reply = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!IsTagged(reply, "OK"))
        {
            throw new BackendAuthenticationRejectedException("The IMAP backend rejected the credential.");
        }
    }

    private static bool IsTagged(string reply, string status)
        => reply.StartsWith($"{Tag} {status}", StringComparison.OrdinalIgnoreCase);
}
