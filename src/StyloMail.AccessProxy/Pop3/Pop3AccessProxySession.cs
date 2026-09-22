using System.Security.Cryptography;
using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Pop3;

/// <summary>
/// Terminates a POP3 client session and relays it to the backend.
/// </summary>
/// <remarks>
/// The same seam and the same lifecycle as <see cref="Imap.ImapAccessProxySession"/>, only the
/// dialect differs. <c>USER</c>/<c>PASS</c> is the classical POP3 authentication and, like IMAP's
/// <c>LOGIN</c>, it is the spelling that stranded clients actually speak, so it is the spelling that
/// matters most here.
///
/// <para>
/// One POP3-specific hazard worth naming: <c>PASS</c> is only meaningful after <c>USER</c>, and the
/// username is what the account is looked up by. The session therefore holds the username between
/// the two commands rather than treating either alone as a credential, a <c>PASS</c> arriving
/// without a preceding <c>USER</c> is a protocol error, not a login attempt with an empty username.
/// </para>
/// </remarks>
public sealed class Pop3AccessProxySession : AccessProxySessionBase
{
    private string? _user;

    public Pop3AccessProxySession(
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

    public override string ProtocolName => "pop3";

    protected override BackendProtocol BackendProtocol => BackendProtocol.Pop3;

    protected override async ValueTask WriteGreetingAsync(CancellationToken cancellationToken)
        => await Writer.WriteLineAsync("+OK StyloMail POP3 ready", cancellationToken).ConfigureAwait(false);

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

            var (verb, argument) = Split(line);

            switch (verb)
            {
                case "USER":
                    _user = argument;
                    await Writer.WriteLineAsync("+OK", cancellationToken).ConfigureAwait(false);
                    continue;

                case "PASS":
                    if (_user is null)
                    {
                        await Writer.WriteLineAsync("-ERR USER first", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return new ClientCredentials
                    {
                        Login = _user,
                        Password = SecretValue.FromUtf8(argument),
                    };

                case "AUTH":
                    return await ReadAuthAsync(reader, argument, cancellationToken).ConfigureAwait(false);

                case "CAPA":
                    // Advertised capabilities describe what this proxy supports before
                    // authentication. USER is offered because it is the path the stranded clients
                    // this feature exists for actually take.
                    await Writer.WriteLineAsync("+OK Capability list follows", cancellationToken).ConfigureAwait(false);
                    await Writer.WriteLineAsync("USER", cancellationToken).ConfigureAwait(false);
                    await Writer.WriteLineAsync("SASL PLAIN", cancellationToken).ConfigureAwait(false);
                    await Writer.WriteLineAsync(".", cancellationToken).ConfigureAwait(false);
                    continue;

                case "QUIT":
                    await Writer.WriteLineAsync("+OK StyloMail signing off", cancellationToken).ConfigureAwait(false);
                    return null;

                case "STLS":
                    await Writer.WriteLineAsync("-ERR STLS unavailable", cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    await Writer.WriteLineAsync("-ERR Not permitted before authentication", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
            }
        }
    }

    private async ValueTask<ClientCredentials?> ReadAuthAsync(
        BoundedLineReader reader,
        string argument,
        CancellationToken cancellationToken)
    {
        var pos = 0;
        SkipSpaces(argument, ref pos);
        var start = pos;
        while (pos < argument.Length && argument[pos] != ' ')
        {
            pos++;
        }

        var mechanism = argument[start..pos].ToUpperInvariant();
        SkipSpaces(argument, ref pos);
        var initial = pos < argument.Length ? argument[pos..].Trim() : null;

        if (mechanism != "PLAIN")
        {
            await Writer.WriteLineAsync("-ERR Unsupported authentication mechanism", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (initial is null)
        {
            await Writer.WriteLineAsync("+ ", cancellationToken).ConfigureAwait(false);
            initial = await reader.ReadLineAsync(Bounds.AuthenticationTimeout, cancellationToken).ConfigureAwait(false);
            if (initial is null)
            {
                return null;
            }
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(initial.Trim());
        }
        catch (FormatException)
        {
            await Writer.WriteLineAsync("-ERR Malformed base64", cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            var firstNul = Array.IndexOf(decoded, (byte)0);
            var secondNul = firstNul < 0 ? -1 : Array.IndexOf(decoded, (byte)0, firstNul + 1);

            if (firstNul < 0 || secondNul < 0)
            {
                await Writer.WriteLineAsync("-ERR Malformed PLAIN response", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            return new ClientCredentials
            {
                Login = Encoding.UTF8.GetString(decoded, firstNul + 1, secondNul - firstNul - 1),
                Password = SecretValue.FromBytes(decoded.AsSpan(secondNul + 1).ToArray()),
            };
        }
        finally
        {
            // The decoded payload held the plaintext password; the SecretValue above holds its own
            // copy, so this one is cleared now rather than left for the collector.
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static (string Verb, string Argument) Split(string line)
    {
        var space = line.IndexOf(' ');
        if (space < 0)
        {
            return (line.ToUpperInvariant(), string.Empty);
        }

        return (line[..space].ToUpperInvariant(), line[(space + 1)..]);
    }

    private static void SkipSpaces(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }
    }

    protected override async ValueTask WriteAuthenticatedAsync(CancellationToken cancellationToken)
        => await Writer.WriteLineAsync("+OK maildrop locked and ready", cancellationToken).ConfigureAwait(false);

    protected override async ValueTask WriteAuthenticationFailedAsync(CancellationToken cancellationToken)
        => await Writer.WriteLineAsync("-ERR Authentication failed", cancellationToken).ConfigureAwait(false);
}
