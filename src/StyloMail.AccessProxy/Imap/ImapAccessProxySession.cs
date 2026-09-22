using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Imap;

/// <summary>
/// Terminates an IMAP client session and relays it to the backend.
/// </summary>
/// <remarks>
/// <b>This is the product argument of spec §9.3 made concrete.</b> Google requires OAuth for IMAP,
/// and a large amount of deployed client software cannot do OAuth — it speaks <c>LOGIN user pass</c>
/// and nothing else. Such a client cannot reach Gmail at all any more. Here it speaks that same
/// command to StyloMail with StyloMail-issued credentials, and StyloMail performs whatever exchange
/// Gmail actually demands on the far side. The proxy does not merely sit in the path; it restores
/// access Google's change removed.
///
/// <para>
/// Which is why this class cares so much about <c>LOGIN</c>: it is not legacy support here, it is
/// the entire point of the feature, and it is the one place a client password passes through the
/// proxy. It is verified and zeroed immediately, never retained, and never forwarded to the backend.
/// </para>
/// </remarks>
public sealed class ImapAccessProxySession : AccessProxySessionBase
{
    private string? _pendingTag;

    public ImapAccessProxySession(
        IStyloMailAccountStore accounts,
        IStyloMailPasswordHasher passwordHasher,
        BackendCredentialResolver credentials,
        IBackendConnector backend,
        SessionLimiter limiter,
        AccessProxyBounds bounds,
        TimeProvider timeProvider,
        IRetrievalObserver? observer = null)
        : base(accounts, passwordHasher, credentials, backend, limiter, bounds, timeProvider, observer)
    {
    }

    public override string ProtocolName => "imap";

    protected override BackendProtocol BackendProtocol => BackendProtocol.Imap;

    protected override async ValueTask WriteGreetingAsync(CancellationToken cancellationToken)
    {
        // The capability list is ours, not the backend's, because this proxy terminates the session
        // and authenticates the client itself. A client that caches it and later issues an extension
        // the backend lacks will get a BAD from the backend — an accepted consequence of session
        // termination, and the same one every authenticating mail proxy has.
        await Writer.WriteLineAsync(
            "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=LOGIN] StyloMail ready",
            cancellationToken).ConfigureAwait(false);
    }

    private protected override async ValueTask<ClientCredentials?> ReadClientCredentialsAsync(
        BoundedLineReader reader,
        CancellationToken cancellationToken)
    {
        for (var commands = 0; ; commands++)
        {
            if (commands >= Bounds.MaxAuthenticationCommands)
            {
                throw new AccessProxyProtocolException(
                    $"Client sent more than {Bounds.MaxAuthenticationCommands} commands without authenticating.");
            }

            var line = await reader
                .ReadLineAsync(Bounds.AuthenticationTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {
                return null;
            }

            if (!ImapLine.TrySplit(line, out var tag, out var verb, out var arguments)
                || !ImapLine.IsValidTag(tag))
            {
                // No tag to answer with, so the only safe reply is untagged and terminal.
                throw new AccessProxyProtocolException("Malformed IMAP command line.");
            }

            switch (verb.ToUpperInvariant())
            {
                case "LOGIN":
                    return await ReadLoginAsync(tag, arguments, cancellationToken).ConfigureAwait(false);

                case "AUTHENTICATE":
                    return await ReadAuthenticateAsync(reader, tag, arguments, cancellationToken)
                        .ConfigureAwait(false);

                case "CAPABILITY":
                    await Writer.WriteLineAsync(
                        "* CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=LOGIN", cancellationToken).ConfigureAwait(false);
                    await Writer.WriteLineAsync($"{tag} OK CAPABILITY completed", cancellationToken)
                        .ConfigureAwait(false);
                    continue;

                case "NOOP":
                    await Writer.WriteLineAsync($"{tag} OK NOOP completed", cancellationToken).ConfigureAwait(false);
                    continue;

                case "LOGOUT":
                    await Writer.WriteLineAsync("* BYE StyloMail closing", cancellationToken).ConfigureAwait(false);
                    await Writer.WriteLineAsync($"{tag} OK LOGOUT completed", cancellationToken).ConfigureAwait(false);
                    return null;

                case "STARTTLS":
                    // TLS is the listener's job, terminated before the session is ever constructed.
                    // Advertising no STARTTLS and refusing it is honest; silently accepting it and
                    // continuing in the clear would not be.
                    await Writer.WriteLineAsync($"{tag} NO STARTTLS unavailable", cancellationToken)
                        .ConfigureAwait(false);
                    continue;

                default:
                    // A command we do not handle before authentication. The client is told rather
                    // than left waiting.
                    await Writer.WriteLineAsync($"{tag} BAD Not permitted before authentication", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
            }
        }
    }

    private async ValueTask<ClientCredentials?> ReadLoginAsync(
        string tag,
        string arguments,
        CancellationToken cancellationToken)
    {
        _pendingTag = tag;
        var pos = 0;

        if (!ImapLine.TryReadArgument(arguments, ref pos, out var login))
        {
            await Writer.WriteLineAsync($"{tag} BAD Malformed LOGIN", cancellationToken).ConfigureAwait(false);
            _pendingTag = null;
            return null;
        }

        if (!ImapLine.TryReadArgument(arguments, ref pos, out var password))
        {
            await Writer.WriteLineAsync($"{tag} BAD Malformed LOGIN", cancellationToken).ConfigureAwait(false);
            _pendingTag = null;
            return null;
        }

        // The password becomes a SecretValue immediately so it can be zeroed the moment verification
        // finishes. It is never stored, never logged, and never forwarded to the backend.
        return new ClientCredentials
        {
            Login = login,
            Password = SecretValue.FromUtf8(password),
        };
    }

    private async ValueTask<ClientCredentials?> ReadAuthenticateAsync(
        BoundedLineReader reader,
        string tag,
        string arguments,
        CancellationToken cancellationToken)
    {
        var pos = 0;
        if (!ImapLine.TryReadArgument(arguments, ref pos, out var mechanism))
        {
            await Writer.WriteLineAsync($"{tag} BAD Malformed AUTHENTICATE", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var upper = mechanism.ToUpperInvariant();

        // Some clients send the initial response on the command line (SASL-IR); others wait for a
        // challenge. Both spellings are accepted.
        string? initial = null;
        if (ImapLine.TryReadArgument(arguments, ref pos, out var inline))
        {
            initial = inline;
        }

        switch (upper)
        {
            case "PLAIN":
                return await ReadPlainAsync(reader, tag, initial, cancellationToken).ConfigureAwait(false);

            case "LOGIN":
                return await ReadLoginMechanismAsync(reader, tag, cancellationToken).ConfigureAwait(false);

            default:
                // Including XOAUTH2: a client that can do OAuth does not need this proxy, so the
                // mechanism is refused rather than half-supported.
                await Writer.WriteLineAsync($"{tag} NO Unsupported authentication mechanism", cancellationToken)
                    .ConfigureAwait(false);
                return null;
        }
    }

    private async ValueTask<ClientCredentials?> ReadPlainAsync(
        BoundedLineReader reader,
        string tag,
        string? initial,
        CancellationToken cancellationToken)
    {
        _pendingTag = tag;

        var payload = initial;
        if (payload is null)
        {
            await Writer.WriteLineAsync("+", cancellationToken).ConfigureAwait(false);
            payload = await reader.ReadLineAsync(Bounds.AuthenticationTimeout, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                _pendingTag = null;
                return null;
            }
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload.Trim());
        }
        catch (FormatException)
        {
            _pendingTag = null;
            await Writer.WriteLineAsync($"{tag} BAD Malformed base64", cancellationToken).ConfigureAwait(false);
            return null;
        }

        // authzid \0 authcid \0 passwd
        var firstNul = Array.IndexOf(decoded, (byte)0);
        var secondNul = firstNul < 0 ? -1 : Array.IndexOf(decoded, (byte)0, firstNul + 1);

        if (firstNul < 0 || secondNul < 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            _pendingTag = null;
            await Writer.WriteLineAsync($"{tag} BAD Malformed PLAIN response", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var login = Encoding.UTF8.GetString(decoded, firstNul + 1, secondNul - firstNul - 1);
        var password = secretFrom(decoded.AsSpan(secondNul + 1));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);

        return new ClientCredentials { Login = login, Password = password };

        static SecretValue secretFrom(ReadOnlySpan<byte> bytes) => SecretValue.FromBytes(bytes.ToArray());
    }

    private async ValueTask<ClientCredentials?> ReadLoginMechanismAsync(
        BoundedLineReader reader,
        string tag,
        CancellationToken cancellationToken)
    {
        _pendingTag = tag;

        var login = await ReadPromptedContinuationAsync(reader, "Username:", cancellationToken)
            .ConfigureAwait(false);
        if (login is null)
        {
            _pendingTag = null;
            return null;
        }

        var password = await ReadPromptedContinuationAsync(reader, "Password:", cancellationToken)
            .ConfigureAwait(false);
        if (password is null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(login);
            _pendingTag = null;
            return null;
        }

        return new ClientCredentials
        {
            Login = Encoding.UTF8.GetString(login),
            Password = SecretValue.FromBytes(password),
        };
    }

    /// <summary>Sends one SASL LOGIN challenge and reads the base64 response to it.</summary>
    private async ValueTask<byte[]?> ReadPromptedContinuationAsync(
        BoundedLineReader reader,
        string prompt,
        CancellationToken cancellationToken)
    {
        // The challenge text is a fixed protocol constant; only the response carries anything.
        await Writer
            .WriteLineAsync($"+ {Convert.ToBase64String(Encoding.ASCII.GetBytes(prompt))}", cancellationToken)
            .ConfigureAwait(false);

        var line = await reader
            .ReadLineAsync(Bounds.AuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (line is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(line.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    protected override async ValueTask WriteAuthenticatedAsync(CancellationToken cancellationToken)
        => await Writer
            .WriteLineAsync($"{_pendingTag ?? "*"} OK LOGIN completed", cancellationToken)
            .ConfigureAwait(false);

    protected override async ValueTask WriteAuthenticationFailedAsync(CancellationToken cancellationToken)
        => await Writer
            .WriteLineAsync($"{_pendingTag ?? "*"} NO Authentication failed", cancellationToken)
            .ConfigureAwait(false);
}
