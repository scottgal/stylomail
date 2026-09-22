using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>
/// Authenticates a POP3 channel to the provider.
/// </summary>
/// <remarks>
/// The same seam as <see cref="ImapBackendConnector"/>, framed for POP3: <c>AUTH</c> for SASL,
/// <c>USER</c>/<c>PASS</c> for a legacy credential pair. Both branches carry opaque tokens and
/// neither knows what kind of credential produced them.
///
/// <para>
/// <b>Both styles are implemented on purpose.</b> POP3's SASL support is uneven across servers,
/// which is why the app-password provider chooses the legacy pair for this protocol while the OAuth
/// provider uses <c>XOAUTH2</c> — a framing decision taken below the seam by the credential
/// provider, not above it by this driver or the session. Having both here is what lets that choice
/// move without touching anything but the provider.
/// </para>
/// </remarks>
public sealed class Pop3BackendConnector : BackendConnectorBase
{
    public Pop3BackendConnector(
        IBackendTransport transport,
        BackendCredentialResolver credentials,
        AccessProxyBounds bounds)
        : base(transport, credentials, bounds)
    {
    }

    public override string ProviderLabel => "pop3-backend";

    protected override BackendProtocol Protocol => BackendProtocol.Pop3;

    private protected override async ValueTask AuthenticateAsync(
        IDuplexChannel channel,
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        await ReadGreetingAsync(reader, ["+OK"], cancellationToken).ConfigureAwait(false);

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
            await writer.WriteLineAsync($"AUTH {mechanism}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using (opening)
            {
                await writer
                    .WriteRawLineAsync(
                        WireCommands.Concatenate($"AUTH {mechanism} ", WireCommands.Base64(opening)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        for (var round = 1; ; round++)
        {
            CheckRoundBudget(round);

            var reply = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);

            if (reply.StartsWith("-ERR", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackendAuthenticationRejectedException("The POP3 backend rejected the credential.");
            }

            if (reply.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (reply.StartsWith('+'))
            {
                // RFC 5034 continuation: "+ " then a base64 challenge, possibly empty.
                var challengeText = reply.Length > 1 && reply[1] == ' ' ? reply[2..] : reply[1..];
                var challenge = WireCommands.DecodeBase64(challengeText, "SASL challenge");

                using var response = await authenticator
                    .NextAsync(challenge, cancellationToken)
                    .ConfigureAwait(false);

                if (response is null)
                {
                    throw new BackendAuthenticationRejectedException(
                        "The credential provider abandoned the authentication exchange.");
                }

                await writer
                    .WriteRawLineAsync(WireCommands.Base64(response), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            throw new AccessProxyProtocolException("The POP3 backend sent an unexpected authentication reply.");
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

        await writer
            .WriteRawLineAsync(WireCommands.VerbWithToken("USER", user), cancellationToken)
            .ConfigureAwait(false);

        if (!IsOk(await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false)))
        {
            throw new BackendAuthenticationRejectedException("The POP3 backend rejected the username.");
        }

        using var password = await authenticator.NextAsync(null, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialUnavailableException("The credential provider produced no password.");

        await writer
            .WriteRawLineAsync(WireCommands.VerbWithToken("PASS", password), cancellationToken)
            .ConfigureAwait(false);

        if (!IsOk(await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false)))
        {
            throw new BackendAuthenticationRejectedException("The POP3 backend rejected the credential.");
        }
    }

    private static bool IsOk(string reply) => reply.StartsWith("+OK", StringComparison.OrdinalIgnoreCase);
}
