using System.Text;
using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;
using StyloMail.AccessProxy.Submission;

namespace StyloMail.AccessProxy.Smtp;

/// <summary>
/// Terminates an SMTP submission client through STARTTLS and authenticates it with a StyloMail
/// account before relaying to an already TLS-authenticated backend connector.
/// </summary>
/// <remarks>
/// This class intentionally permits no SMTP AUTH before the channel has completed STARTTLS. A
/// listener must provide <see cref="IStartTlsCapableChannel"/> backed by Host-supplied TLS material;
/// an ordinary plaintext channel receives a transient TLS-unavailable reply and cannot downgrade to
/// cleartext authentication.
/// </remarks>
public sealed class SmtpAccessProxySession
{
    private readonly string _tenantId;
    private readonly IStyloMailAccountStore _accounts;
    private readonly IStyloMailPasswordHasher _passwordHasher;
    private readonly IBackendConnector _backend;
    private readonly SessionLimiter _limiter;
    private readonly AccessProxyBounds _bounds;
    private readonly TimeProvider _timeProvider;
    private readonly IProxySubmissionAssessment _assessment;
    private readonly IEphemeralSubmissionStager _stager;

    public SmtpAccessProxySession(
        string tenantId,
        IStyloMailAccountStore accounts,
        IStyloMailPasswordHasher passwordHasher,
        IBackendConnector backend,
        SessionLimiter limiter,
        AccessProxyBounds bounds,
        TimeProvider timeProvider,
        IProxySubmissionAssessment assessment,
        IEphemeralSubmissionStager stager)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        _tenantId = tenantId;
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _bounds.Validate();
    }

    public async Task<SessionOutcome> RunAsync(IDuplexChannel initialClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialClient);
        using var globalSlot = await _limiter.AcquireGlobalAsync(cancellationToken).ConfigureAwait(false);
        var client = initialClient;
        var reader = new BoundedLineReader(client.Input, _bounds, _timeProvider);
        var writer = new ProtocolLineWriter(client.Output);
        await writer.WriteLineAsync("220 StyloMail submission ready", cancellationToken).ConfigureAwait(false);

        var greeted = false;
        using var authentication = new TimeoutScope(_bounds.AuthenticationTimeout, _timeProvider, cancellationToken);
        for (var commands = 0; commands < _bounds.MaxAuthenticationCommands; commands++)
        {
            var line = await reader.ReadLineAsync(_bounds.AuthenticationTimeout, authentication.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                return SessionOutcome.ClientDisconnected;
            }

            var (verb, arguments) = Split(line);
            switch (verb)
            {
                case "EHLO":
                    greeted = true;
                    var secure = client is ISecureDuplexChannel { IsSecure: true };
                    await writer.WriteLineAsync("250-StyloMail", cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(
                        secure ? "250 AUTH PLAIN LOGIN" : "250 STARTTLS",
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "HELO":
                    greeted = true;
                    await writer.WriteLineAsync("250 StyloMail", cancellationToken).ConfigureAwait(false);
                    break;
                case "NOOP":
                    await writer.WriteLineAsync("250 OK", cancellationToken).ConfigureAwait(false);
                    break;
                case "RSET":
                    await writer.WriteLineAsync("250 OK", cancellationToken).ConfigureAwait(false);
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Bye", cancellationToken).ConfigureAwait(false);
                    return SessionOutcome.ClientDisconnected;
                case "STARTTLS" when greeted && client is IStartTlsCapableChannel startTls:
                    await writer.WriteLineAsync("220 Ready to start TLS", cancellationToken).ConfigureAwait(false);
                    client = await startTls.UpgradeToTlsAsync(authentication.Token).ConfigureAwait(false);
                    reader = new BoundedLineReader(client.Input, _bounds, _timeProvider);
                    writer = new ProtocolLineWriter(client.Output);
                    greeted = false;
                    break;
                case "STARTTLS":
                    await writer.WriteLineAsync("454 TLS unavailable", cancellationToken).ConfigureAwait(false);
                    break;
                case "AUTH" when greeted && client is ISecureDuplexChannel { IsSecure: true }:
                    var credentials = await ReadAuthAsync(arguments, reader, writer, authentication.Token).ConfigureAwait(false);
                    if (credentials is null)
                    {
                        await writer.WriteLineAsync("535 Authentication failed", cancellationToken).ConfigureAwait(false);
                        return SessionOutcome.ClientRejected;
                    }

                    using (credentials)
                    {
                        var account = await _accounts.FindByLoginAsync(_tenantId, credentials.Login, cancellationToken)
                            .ConfigureAwait(false);
                        var valid = account is { Disabled: false }
                            ? _passwordHasher.Verify(credentials.Password.Utf8, account.PasswordHash)
                            : _passwordHasher.Verify(credentials.Password.Utf8, _passwordHasher.DecoyVerifier);
                        if (!valid || account is null || account.Disabled)
                        {
                            await writer.WriteLineAsync("535 Authentication failed", cancellationToken).ConfigureAwait(false);
                            return SessionOutcome.ClientRejected;
                        }

                        using var accountSlot = _limiter.TryAcquireAccount(account.AccountId);
                        if (accountSlot is null)
                        {
                            await writer.WriteLineAsync("535 Authentication failed", cancellationToken).ConfigureAwait(false);
                            return SessionOutcome.AccountLimitReached;
                        }

                        await writer.WriteLineAsync("235 Authentication successful", cancellationToken).ConfigureAwait(false);
                        await using var backend = await _backend.ConnectAsync(new BackendConnectionRequest
                        {
                            TenantId = account.TenantId,
                            AccountId = account.BackendAccountId,
                            Protocol = BackendProtocol.Smtp,
                            TimeProvider = _timeProvider,
                        }, cancellationToken).ConfigureAwait(false);
                        return await RunTransactionAsync(client, backend, reader, writer, cancellationToken)
                            .ConfigureAwait(false);
                    }
                case "AUTH":
                    await writer.WriteLineAsync("538 Encryption required for requested authentication mechanism", cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    await writer.WriteLineAsync("503 Send EHLO, STARTTLS, then AUTH", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return SessionOutcome.ProtocolError;
    }

    private async ValueTask<ClientCredentials?> ReadAuthAsync(
        string arguments,
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        CancellationToken cancellationToken)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        if (string.Equals(parts[0], "PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            var initial = parts.Length == 2 ? parts[1] : null;
            if (initial is null)
            {
                await writer.WriteLineAsync("334 ", cancellationToken).ConfigureAwait(false);
                initial = await reader.ReadLineAsync(_bounds.AuthenticationTimeout, cancellationToken).ConfigureAwait(false);
            }

            return DecodePlain(initial);
        }

        if (!string.Equals(parts[0], "LOGIN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await writer.WriteLineAsync("334 VXNlcm5hbWU6", cancellationToken).ConfigureAwait(false);
        var encodedLogin = await reader.ReadLineAsync(_bounds.AuthenticationTimeout, cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("334 UGFzc3dvcmQ6", cancellationToken).ConfigureAwait(false);
        var encodedPassword = await reader.ReadLineAsync(_bounds.AuthenticationTimeout, cancellationToken).ConfigureAwait(false);
        if (encodedLogin is null || encodedPassword is null)
        {
            return null;
        }

        try
        {
            return new ClientCredentials
            {
                Login = Encoding.UTF8.GetString(Convert.FromBase64String(encodedLogin)),
                Password = SecretValue.FromBytes(Convert.FromBase64String(encodedPassword)),
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ClientCredentials? DecodePlain(string? encoded)
    {
        if (encoded is null)
        {
            return null;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
        try
        {
            var first = Array.IndexOf(bytes, (byte)0);
            var second = first < 0 ? -1 : Array.IndexOf(bytes, (byte)0, first + 1);
            if (second < 0 || second == bytes.Length - 1)
            {
                return null;
            }

            return new ClientCredentials
            {
                Login = Encoding.UTF8.GetString(bytes, first + 1, second - first - 1),
                Password = SecretValue.FromBytes(bytes[(second + 1)..]),
            };
        }
        finally { Array.Clear(bytes); }
    }

    private static (string Verb, string Arguments) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0
            ? (line.ToUpperInvariant(), string.Empty)
            : (line[..space].ToUpperInvariant(), line[(space + 1)..].Trim());
    }

    private async Task<SessionOutcome> RunTransactionAsync(
        IDuplexChannel client,
        IDuplexChannel backend,
        BoundedLineReader clientReader,
        ProtocolLineWriter clientWriter,
        CancellationToken cancellationToken)
    {
        var backendReader = new BoundedLineReader(backend.Input, _bounds, _timeProvider);
        var backendWriter = new ProtocolLineWriter(backend.Output);
        var hasMail = false;
        var recipients = 0;

        while (true)
        {
            var line = await clientReader.ReadLineAsync(_bounds.IdleTimeout, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return SessionOutcome.ClientDisconnected;
            }

            var (verb, _) = Split(line);
            switch (verb)
            {
                case "QUIT":
                    await backendWriter.WriteLineAsync("QUIT", cancellationToken).ConfigureAwait(false);
                    await clientWriter.WriteLineAsync("221 Bye", cancellationToken).ConfigureAwait(false);
                    return SessionOutcome.Relayed;
                case "NOOP":
                case "RSET":
                    await RelayCommandAsync(line, backendReader, backendWriter, clientWriter, cancellationToken)
                        .ConfigureAwait(false);
                    if (verb == "RSET") { hasMail = false; recipients = 0; }
                    break;
                case "MAIL":
                    await RelayCommandAsync(line, backendReader, backendWriter, clientWriter, cancellationToken)
                        .ConfigureAwait(false);
                    hasMail = true; recipients = 0;
                    break;
                case "RCPT" when hasMail:
                    await RelayCommandAsync(line, backendReader, backendWriter, clientWriter, cancellationToken)
                        .ConfigureAwait(false);
                    recipients++;
                    break;
                case "DATA" when hasMail && recipients > 0:
                    var outcome = await AssessThenRelayDataAsync(
                        clientReader, clientWriter, backendReader, backendWriter, cancellationToken).ConfigureAwait(false);
                    hasMail = false; recipients = 0;
                    if (outcome != SessionOutcome.Relayed) return outcome;
                    break;
                default:
                    await clientWriter.WriteLineAsync("503 Bad sequence of commands", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task RelayCommandAsync(
        string command,
        BoundedLineReader backendReader,
        ProtocolLineWriter backendWriter,
        ProtocolLineWriter clientWriter,
        CancellationToken cancellationToken)
    {
        await backendWriter.WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
        var reply = await backendReader.ReadLineAsync(_bounds.BackendAuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false) ?? throw new AccessProxyProtocolException("SMTP backend closed during a transaction.");
        await clientWriter.WriteLineAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionOutcome> AssessThenRelayDataAsync(
        BoundedLineReader clientReader,
        ProtocolLineWriter clientWriter,
        BoundedLineReader backendReader,
        ProtocolLineWriter backendWriter,
        CancellationToken cancellationToken)
    {
        await clientWriter.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>", cancellationToken).ConfigureAwait(false);
        await using var staged = await _stager.CreateAsync(_bounds.MaxSubmissionBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            while (true)
            {
                var line = await clientReader.ReadLineAsync(_bounds.IdleTimeout, cancellationToken).ConfigureAwait(false);
                if (line is null) return SessionOutcome.ClientDisconnected;
                if (line == ".") break;
                var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                try
                {
                    if (staged.Length + bytes.Length > _bounds.MaxSubmissionBytes)
                    {
                        await clientWriter.WriteLineAsync("552 Message exceeds proxy staging limit", cancellationToken).ConfigureAwait(false);
                        return SessionOutcome.ProtocolError;
                    }
                    await staged.Content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                finally { Array.Clear(bytes); }
            }

            staged.Content.Position = 0;
            var assessment = await _assessment.AssessAsync(staged, cancellationToken).ConfigureAwait(false);
            if (assessment.Disposition == ProxySubmissionDisposition.Refuse && assessment.IsDeterministic)
            {
                await clientWriter.WriteLineAsync("554 Submission refused by local policy", cancellationToken).ConfigureAwait(false);
                return SessionOutcome.ProtocolError;
            }

            await backendWriter.WriteLineAsync("DATA", cancellationToken).ConfigureAwait(false);
            var ready = await backendReader.ReadLineAsync(_bounds.BackendAuthenticationTimeout, cancellationToken)
                .ConfigureAwait(false) ?? throw new AccessProxyProtocolException("SMTP backend closed before DATA.");
            if (!ready.StartsWith("354", StringComparison.Ordinal))
            {
                await clientWriter.WriteLineAsync(ready, cancellationToken).ConfigureAwait(false);
                return SessionOutcome.ProtocolError;
            }

            await backendWriter.WriteStreamAsync(staged.Content, cancellationToken).ConfigureAwait(false);
            await backendWriter.WriteLineAsync(".", cancellationToken).ConfigureAwait(false);
            var accepted = await backendReader.ReadLineAsync(_bounds.BackendAuthenticationTimeout, cancellationToken)
                .ConfigureAwait(false) ?? throw new AccessProxyProtocolException("SMTP backend closed after DATA.");
            await clientWriter.WriteLineAsync(accepted, cancellationToken).ConfigureAwait(false);
            return SessionOutcome.Relayed;
        }
        finally
        {
            // The stager handle is disposed on allow, refusal, abort and exceptions. Its contract
            // also requires TTL cleanup for a process crash before this finally can execute.
        }
    }
}
