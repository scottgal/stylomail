using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>Authenticates an already TLS-protected SMTP submission channel to its provider.</summary>
/// <remarks>
/// The Host transport must complete backend TLS before returning the channel. This connector never
/// falls back to a clear SMTP AUTH exchange and refuses the legacy USER/PASS framing outright.
/// </remarks>
public sealed class SmtpBackendConnector : BackendConnectorBase
{
    public SmtpBackendConnector(
        IBackendTransport transport,
        BackendCredentialResolver credentials,
        AccessProxyBounds bounds)
        : base(transport, credentials, bounds)
    {
    }

    public override string ProviderLabel => "smtp-submission-backend";

    protected override BackendProtocol Protocol => BackendProtocol.Smtp;

    private protected override async ValueTask AuthenticateAsync(
        IDuplexChannel channel,
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        if (channel is not ISecureDuplexChannel { IsSecure: true })
        {
            throw new CredentialUnavailableException(
                "SMTP backend transport did not establish TLS. Refusing SMTP AUTH on plaintext.");
        }

        if (authenticator.Style != BackendAuthStyle.Sasl)
        {
            throw new CredentialUnavailableException(
                "SMTP submission accepts SASL credentials only; USER/PASS framing is unsupported.");
        }

        var greeting = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!greeting.StartsWith("220", StringComparison.Ordinal))
        {
            throw new BackendAuthenticationRejectedException("The SMTP backend did not send a 220 greeting.");
        }

        await writer.WriteLineAsync("EHLO stylomail", cancellationToken).ConfigureAwait(false);
        var capabilities = await ReadEhloAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!capabilities.Any(line => line.StartsWith("250-AUTH", StringComparison.OrdinalIgnoreCase)
                                  || line.StartsWith("250 AUTH", StringComparison.OrdinalIgnoreCase)))
        {
            throw new BackendAuthenticationRejectedException("The TLS SMTP backend did not advertise AUTH.");
        }

        var mechanism = authenticator.SaslMechanism
            ?? throw new CredentialUnavailableException("The credential provider named no SASL mechanism.");
        using var opening = await authenticator.NextAsync(null, cancellationToken).ConfigureAwait(false)
            ?? throw new CredentialUnavailableException("The credential provider produced no SMTP SASL opening token.");

        await writer.WriteRawLineAsync(
            WireCommands.Concatenate($"AUTH {mechanism} ", WireCommands.Base64(opening)), cancellationToken)
            .ConfigureAwait(false);

        for (var round = 1; ; round++)
        {
            CheckRoundBudget(round);
            var reply = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);
            if (reply.StartsWith("235", StringComparison.Ordinal))
            {
                return;
            }

            if (reply.StartsWith("334", StringComparison.Ordinal))
            {
                var encodedChallenge = reply.Length > 4 ? reply[4..].Trim() : string.Empty;
                var challenge = WireCommands.DecodeBase64(encodedChallenge, "SMTP SASL challenge");
                using var response = await authenticator.NextAsync(challenge, cancellationToken).ConfigureAwait(false)
                    ?? throw new BackendAuthenticationRejectedException("The credential provider abandoned SMTP SASL.");
                await writer.WriteRawLineAsync(WireCommands.Base64(response), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new BackendAuthenticationRejectedException("The SMTP backend rejected the credential.");
        }
    }

    private async ValueTask<List<string>> ReadEhloAsync(BoundedLineReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        for (var count = 0; ; count++)
        {
            CheckRoundBudget(count + 1);
            var line = await ReadReplyAsync(reader, cancellationToken).ConfigureAwait(false);
            if (!line.StartsWith("250", StringComparison.Ordinal))
            {
                throw new BackendAuthenticationRejectedException("The SMTP backend rejected EHLO.");
            }

            lines.Add(line);
            if (line.Length < 4 || line[3] != '-')
            {
                return lines;
            }
        }
    }
}
