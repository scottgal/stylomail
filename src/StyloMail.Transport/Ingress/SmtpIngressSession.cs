using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Transport.Ingress;

/// <summary>
/// The server side of one SMTP conversation: reading commands, deciding what is authorised, and
/// answering <c>250</c> only once a durable queue row exists.
/// </summary>
/// <remarks>
/// <para>
/// The authorisation model is two rules and nothing else:
/// </para>
/// <list type="bullet">
/// <item><b>Authenticated</b>, the principal may send mail, but only with a sender identity it is
/// authorised to use, and not at all until the connection is encrypted.</item>
/// <item><b>Unauthenticated</b>, mail is accepted only for a recipient domain this deployment
/// serves. Any other recipient is a third party trying to relay, and is refused.</item>
/// </list>
/// <para>
/// Together those mean no sequence of commands turns this listener into an open relay: an
/// unauthenticated client cannot reach a foreign domain, and an authenticated one cannot forge a
/// sender it does not own.
/// </para>
/// <para>
/// <b>Nothing here reads an <c>Authentication-Results</c> header.</b> A message cannot assert its own
/// authentication. Results are only ever accepted from a configured trusted boundary verifier, and
/// this listener is not one, it is downstream of the MTA that actually saw the client connection,
/// so it has neither the connecting address nor the domain's policy. Provenance is therefore
/// recorded as incomplete rather than assumed clean.
/// </para>
/// </remarks>
internal sealed class SmtpIngressSession
{
    private const int MaxCommandLineBytes = 4096;

    private readonly SmtpIngressOptions _options;
    private readonly ISmtpIngressSink _sink;
    private readonly ISubmissionAuthenticator? _authenticator;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _readBuffer = new byte[8192];
    private readonly List<string> _recipients = [];

    private readonly string? _remoteAddress;

    private Stream _stream;
    private int _readPosition;
    private int _readLength;
    private string? _clientName;
    private bool _encrypted;
    private bool _helloSeen;
    private int _commandsSeen;
    private int _authFailures;
    private AuthenticatedPrincipal? _principal;
    private string? _mailFrom;
    private MailDirection _direction = MailDirection.Inbound;

    private SmtpIngressSession(
        Stream stream,
        SmtpIngressOptions options,
        ISmtpIngressSink sink,
        ISubmissionAuthenticator? authenticator,
        TimeProvider timeProvider,
        string? remoteAddress)
    {
        _stream = stream;
        _options = options;
        _sink = sink;
        _authenticator = authenticator;
        _timeProvider = timeProvider;
        _remoteAddress = remoteAddress;
    }

    internal static async Task RunAsync(
        TcpClient client,
        SmtpIngressOptions options,
        ISmtpIngressSink sink,
        ISubmissionAuthenticator? authenticator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;

            var session = new SmtpIngressSession(
                client.GetStream(),
                options,
                sink,
                authenticator,
                timeProvider,
                remote?.Address.ToString());

            try
            {
                await session.ServeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException
                or ObjectDisposedException or AuthenticationException or SocketException)
            {
                // A client that disappears mid-conversation is ordinary. Nothing was accepted that
                // was not already durable, so there is nothing to unwind.
            }
        }
    }

    private async ValueTask ServeAsync(CancellationToken cancellationToken)
    {
        await ReplyAsync(220, $"{_options.ServerName} ESMTP StyloMail ready", cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(_options.CommandTimeout, MaxCommandLineBytes, cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {
                return;
            }

            _commandsSeen++;
            if (_commandsSeen > _options.MaxCommandsPerSession)
            {
                // A client that will not stop talking is holding a connection slot. Bound it.
                await ReplyAsync(421, "4.7.0 Too many commands on this connection", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var command = Encoding.UTF8.GetString(line).Trim();
            var verb = VerbOf(command);
            var argument = ArgumentOf(command);

            switch (verb.ToUpperInvariant())
            {
                case "EHLO":
                    _clientName = argument;
                    await HandleEhloAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "HELO":
                    _clientName = argument;
                    _helloSeen = true;
                    await ReplyAsync(250, _options.ServerName, cancellationToken).ConfigureAwait(false);
                    break;

                case "STARTTLS":
                    if (!await HandleStartTlsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case "AUTH":
                    await HandleAuthAsync(argument, cancellationToken).ConfigureAwait(false);
                    break;

                case "MAIL":
                    await HandleMailFromAsync(argument, cancellationToken).ConfigureAwait(false);
                    break;

                case "RCPT":
                    await HandleRcptToAsync(argument, cancellationToken).ConfigureAwait(false);
                    break;

                case "DATA":
                    await HandleDataAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "RSET":
                    ResetTransaction();
                    await ReplyAsync(250, "2.0.0 Reset", cancellationToken).ConfigureAwait(false);
                    break;

                case "NOOP":
                    await ReplyAsync(250, "2.0.0 OK", cancellationToken).ConfigureAwait(false);
                    break;

                case "QUIT":
                    await ReplyAsync(221, $"2.0.0 {_options.ServerName} closing connection", cancellationToken)
                        .ConfigureAwait(false);
                    return;

                // VRFY and EXPN are address-harvesting surfaces. RFC 5321 permits refusing them and
                // nothing here needs them; answering non-committally tells a prober nothing.
                case "VRFY":
                case "EXPN":
                    await ReplyAsync(252, "2.5.2 Cannot verify, but will attempt delivery", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    await ReplyAsync(500, "5.5.1 Command unrecognised", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async ValueTask HandleEhloAsync(CancellationToken cancellationToken)
    {
        _helloSeen = true;
        ResetTransaction();

        var capabilities = new List<string>
        {
            $"SIZE {_options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}",
            "8BITMIME",
        };

        if (!_encrypted && _options.Certificate is not null)
        {
            capabilities.Add("STARTTLS");
        }

        // AUTH is advertised only once the connection is encrypted. Advertising it earlier would
        // invite a client to send credentials we would then refuse, and the refusal would still
        // have disclosed the mechanism to anyone watching.
        if (_encrypted && _authenticator is not null)
        {
            capabilities.Add("AUTH PLAIN LOGIN");
        }

        var lines = new List<string> { $"250-{_options.ServerName}" };
        for (var i = 0; i < capabilities.Count; i++)
        {
            lines.Add(i == capabilities.Count - 1 ? $"250 {capabilities[i]}" : $"250-{capabilities[i]}");
        }

        await ReplyAsync(lines, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> HandleStartTlsAsync(CancellationToken cancellationToken)
    {
        if (_options.Certificate is null)
        {
            await ReplyAsync(502, "5.5.1 STARTTLS is not available", cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_encrypted)
        {
            await ReplyAsync(503, "5.5.1 STARTTLS has already been issued", cancellationToken).ConfigureAwait(false);
            return true;
        }

        await ReplyAsync(220, "2.0.0 Ready to start TLS", cancellationToken).ConfigureAwait(false);

        // Anything the client pipelined behind STARTTLS is plaintext and untrusted; it is discarded
        // rather than carried across the handshake. A client that pipelined will simply be waiting
        // for a reply that never comes, and time out, which is the safe outcome, not a silent one.
        _readPosition = 0;
        _readLength = 0;

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: true);

        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _options.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            // A failed handshake leaves the connection unusable. There is no plaintext session to
            // fall back to, and falling back would be exactly the downgrade this prevents.
            await ssl.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _stream = ssl;
        _encrypted = true;

        // RFC 3207 §4.2: everything learned before the handshake is discarded, and the client must
        // EHLO again. Until it does, MAIL is refused by the _helloSeen guard.
        _helloSeen = false;
        ResetTransaction();
        return true;
    }

    private async ValueTask HandleAuthAsync(string argument, CancellationToken cancellationToken)
    {
        if (_authenticator is null)
        {
            await ReplyAsync(503, "5.5.1 Authentication is not available", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_encrypted)
        {
            // Checked here independently of whether AUTH was advertised, because a client may simply
            // try it. A credential must never be read from a stream we are not protecting.
            await ReplyAsync(538, "5.7.11 Encryption required for requested authentication mechanism", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_principal is not null)
        {
            await ReplyAsync(503, "5.5.1 Already authenticated", cancellationToken).ConfigureAwait(false);
            return;
        }

        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        var mechanism = (space < 0 ? argument : argument[..space]).Trim();
        var initial = space < 0 ? string.Empty : argument[(space + 1)..].Trim();

        if (!mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase)
            && !mechanism.Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(504, "5.5.4 Unrecognised authentication mechanism", cancellationToken).ConfigureAwait(false);
            return;
        }

        var credentials = mechanism.Equals("PLAIN", StringComparison.OrdinalIgnoreCase)
            ? await ReadPlainCredentialsAsync(initial, cancellationToken).ConfigureAwait(false)
            : await ReadLoginCredentialsAsync(cancellationToken).ConfigureAwait(false);

        if (credentials is null)
        {
            await FailAuthAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var principal = await _authenticator
            .AuthenticateAsync(credentials.Value.Username, credentials.Value.Password, cancellationToken)
            .ConfigureAwait(false);

        if (principal is null)
        {
            await FailAuthAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _principal = principal;
        await ReplyAsync(235, "2.7.0 Authentication successful", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(string Username, string Password)?> ReadPlainCredentialsAsync(
        string initial,
        CancellationToken cancellationToken)
    {
        var payload = initial;

        if (payload.Length == 0)
        {
            await ReplyAsync(334, string.Empty, cancellationToken).ConfigureAwait(false);

            var line = await ReadLineAsync(_options.CommandTimeout, MaxCommandLineBytes, cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {
                return null;
            }

            payload = Encoding.UTF8.GetString(line).Trim();
        }

        return DecodeSaslPlain(payload);
    }

    private async ValueTask<(string Username, string Password)?> ReadLoginCredentialsAsync(
        CancellationToken cancellationToken)
    {
        await ReplyAsync(334, Convert.ToBase64String(Encoding.UTF8.GetBytes("Username:")), cancellationToken)
            .ConfigureAwait(false);

        var userLine = await ReadLineAsync(_options.CommandTimeout, MaxCommandLineBytes, cancellationToken)
            .ConfigureAwait(false);
        if (userLine is null)
        {
            return null;
        }

        await ReplyAsync(334, Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")), cancellationToken)
            .ConfigureAwait(false);

        var passLine = await ReadLineAsync(_options.CommandTimeout, MaxCommandLineBytes, cancellationToken)
            .ConfigureAwait(false);
        if (passLine is null)
        {
            return null;
        }

        var username = DecodeBase64(userLine);
        var password = DecodeBase64(passLine);

        return username is null || password is null ? null : (username, password);
    }

    private async ValueTask FailAuthAsync(CancellationToken cancellationToken)
    {
        _authFailures++;

        if (_authFailures >= _options.MaxAuthAttempts)
        {
            // An unbounded AUTH loop is a credential-guessing surface that holds a worker slot while
            // it runs. Close the connection rather than keep answering.
            await ReplyAsync(421, "4.7.0 Too many authentication failures", cancellationToken).ConfigureAwait(false);
            throw new AuthenticationException("Too many authentication failures.");
        }

        await ReplyAsync(535, "5.7.8 Authentication credentials invalid", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleMailFromAsync(string argument, CancellationToken cancellationToken)
    {
        if (!_helloSeen)
        {
            await ReplyAsync(503, "5.5.1 Send HELO or EHLO first", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_encrypted && _options.RequireEncryption)
        {
            await ReplyAsync(530, "5.7.0 Must issue a STARTTLS command first", cancellationToken).ConfigureAwait(false);
            return;
        }

        var sender = ExtractPath(argument);
        if (sender is null)
        {
            await ReplyAsync(501, "5.5.4 Syntax: MAIL FROM:<address>", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_principal is not null)
        {
            if (!_principal.MaySendAs(sender))
            {
                // Authenticating proves who you are, not that you may claim any sender. Without this
                // every valid account would be a forgery primitive.
                await ReplyAsync(
                    553, "5.7.1 Sender address rejected: not authorised for this principal", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            _direction = MailDirection.Outbound;
        }
        else
        {
            if (!_options.AllowUnauthenticatedInbound)
            {
                await ReplyAsync(530, "5.7.0 Authentication required", cancellationToken).ConfigureAwait(false);
                return;
            }

            _direction = MailDirection.Inbound;
        }

        ResetTransaction();
        _mailFrom = sender;

        await ReplyAsync(250, "2.1.0 Sender OK", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleRcptToAsync(string argument, CancellationToken cancellationToken)
    {
        if (_mailFrom is null)
        {
            await ReplyAsync(503, "5.5.1 Need MAIL before RCPT", cancellationToken).ConfigureAwait(false);
            return;
        }

        var recipient = ExtractPath(argument);
        if (recipient is null || recipient.Length == 0)
        {
            await ReplyAsync(501, "5.5.4 Syntax: RCPT TO:<address>", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_recipients.Count >= _options.MaxRecipientsPerTransaction)
        {
            await ReplyAsync(452, "4.5.3 Too many recipients", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_principal is null && !_options.RecipientDomains.Allows(recipient))
        {
            // The open-relay refusal. An unauthenticated client may deliver only to a domain we
            // serve; anything else is a third party using us to reach a stranger.
            await ReplyAsync(550, "5.7.1 Relay access denied", cancellationToken).ConfigureAwait(false);
            return;
        }

        _recipients.Add(recipient);
        await ReplyAsync(250, "2.1.5 Recipient OK", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleDataAsync(CancellationToken cancellationToken)
    {
        if (_mailFrom is null || _recipients.Count == 0)
        {
            await ReplyAsync(503, "5.5.1 Need MAIL and RCPT before DATA", cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(354, "End data with <CR><LF>.<CR><LF>", cancellationToken).ConfigureAwait(false);

        var (body, tooLarge) = await ReadDataAsync(cancellationToken).ConfigureAwait(false);

        if (tooLarge)
        {
            // The limit was passed, so we stop reading and let the connection close. Everything the
            // client still has queued would otherwise be read as commands, and there is no safe way
            // to resynchronise part-way through a body.
            await ReplyAsync(552, "5.3.4 Message size exceeds fixed maximum message size", cancellationToken)
                .ConfigureAwait(false);
            throw new IOException("Message exceeded the configured size limit.");
        }

        if (body is null)
        {
            throw new IOException("The client disconnected part-way through the message body.");
        }

        var decision = await EvaluateAsync(body, cancellationToken).ConfigureAwait(false);

        if (!decision.IsAcceptanceValid)
        {
            // A sink claiming acceptance without naming a durable row is a bug in the sink, and the
            // one bug that destroys mail: a 250 tells the client to delete its copy. Downgraded
            // rather than trusted.
            decision = IngressDecision.Defer(
                "The acceptance did not name a durable queue row, so responsibility was not transferred.");
        }

        await ReplyAsync(decision.ReplyCode, FormatDecision(decision), cancellationToken).ConfigureAwait(false);

        ResetTransaction();
    }

    private async ValueTask<IngressDecision> EvaluateAsync(byte[] body, CancellationToken cancellationToken)
    {
        var facts = TransportHeaderScanner.Scan(body, _options.HeaderLimits, _options.LocalHostIdentities);

        if (facts.BoundExceeded is not null)
        {
            return IngressDecision.Reject(
                554, "5.3.0", $"The message header block exceeds the configured limit ({facts.BoundExceeded}).");
        }

        if (facts.LoopDetected)
        {
            // A Received header names us as the host that accepted this message, so it has already
            // been through this system. Delivering it would circulate it indefinitely.
            return IngressDecision.Reject(
                554, "5.4.6", "Message loop detected: a Received header names this system as the receiving host.");
        }

        if (facts.ReceivedCount >= _options.MaxHops)
        {
            return IngressDecision.Reject(
                554, "5.4.6",
                $"Too many hops: the message already carries {facts.ReceivedCount.ToString(CultureInfo.InvariantCulture)} " +
                $"Received headers, at or over the limit of {_options.MaxHops.ToString(CultureInfo.InvariantCulture)}.");
        }

        var internalMessageId = "msg_" + Guid.NewGuid().ToString("N");

        // Our hop is recorded on the way in, which is where we became responsible for the message.
        // It is deliberately not added again on the way out: the delivery port relays a stored
        // message, and marking one hop twice would double-count it for every downstream reader and
        // for our own loop guard.
        var stamped = ReceivedHeader.Prepend(
            body,
            ReceivedHeader.Build(new ReceivedHeaderStamp
            {
                ByHost = _options.ServerName,
                FromHost = _clientName,
                FromAddress = _remoteAddress,
                Protocol = "ESMTP",
                HopId = internalMessageId,
                At = _timeProvider.GetUtcNow(),
            }));

        var submission = new IngressSubmission
        {
            InternalMessageId = internalMessageId,
            TenantId = _principal?.TenantId ?? _options.InboundTenantId,
            Direction = _direction,
            TrustedPrincipalId = _principal?.PrincipalId ?? $"smtp:{_options.ServerName}",
            MailFrom = _mailFrom!,
            Recipients = [.. _recipients],
            RawMessage = stamped,
            Authentication = BuildAuthenticationContext(),
            HopCount = facts.ReceivedCount,
            UntrustedMessageIdHeader = facts.UntrustedMessageId,
        };

        try
        {
            return await _sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defence in depth for the rule that matters most: a sink that fails in a way it did not
            // classify must still not become a 250. Deferring leaves the message with the client,
            // which is always recoverable; accepting would not be.
            return IngressDecision.Defer(
                $"The message could not be made durable ({ex.GetType().Name}). Not accepted.");
        }
    }

    /// <summary>
    /// Builds provenance for the message.
    /// </summary>
    /// <remarks>
    /// Nothing here is derived from the message. An SMTP connection gives us an envelope sender and,
    /// when authenticated, an account, no more. No <c>Authentication-Results</c> header is read and
    /// none would be trusted: results are only accepted from configured trusted boundary verifiers,
    /// and a header the message author wrote is not one.
    ///
    /// <para>
    /// SPF is the clearest case. It needs the original client's connecting address and the domain's
    /// published policy, and neither is ours to see, we are downstream of the MTA that received the
    /// connection, so evaluating it here would check our own address instead. Provenance is recorded
    /// as incomplete so that downstream evidence reports a gap rather than assuming the best.
    /// </para>
    /// </remarks>
    private AuthenticationContext BuildAuthenticationContext() => new()
    {
        ConnectingIp = null,
        AuthenticatedAccount = _principal?.PrincipalId,
        Results = [],
        ApprovedSenderIdentities = _principal?.ApprovedSenderIdentities ?? [],
        ProvenanceIncomplete = true,
    };

    private static string FormatDecision(IngressDecision decision)
    {
        var text = decision.Outcome == IngressOutcome.Accepted && decision.QueueId is not null
            ? $"Ok: queued as {decision.QueueId}"
            : decision.Reason ?? "Refused";

        return $"{decision.EnhancedStatusCode} {text}";
    }

    private void ResetTransaction()
    {
        _mailFrom = null;
        _recipients.Clear();
    }

    /// <summary>Reads the body up to the terminating dot, undoing dot-stuffing.</summary>
    private async ValueTask<(byte[]? Body, bool TooLarge)> ReadDataAsync(CancellationToken cancellationToken)
    {
        var body = new List<byte>();
        long total = 0;
        var lineLimit = (int)Math.Min(_options.MaxMessageBytes, int.MaxValue);

        while (true)
        {
            var line = await ReadLineAsync(_options.DataTimeout, lineLimit, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return (null, false);
            }

            if (line.Length == 1 && line[0] == (byte)'.')
            {
                return ([.. body], false);
            }

            // Undo stuffing: a leading dot was doubled by the sender so it could not be read as the
            // terminator. Removing exactly one dot restores the original bytes.
            var start = line.Length > 0 && line[0] == (byte)'.' ? 1 : 0;

            total += line.Length - start + 2;
            if (total > _options.MaxMessageBytes)
            {
                return (null, true);
            }

            body.AddRange(line.AsSpan(start));
            body.Add((byte)'\r');
            body.Add((byte)'\n');
        }
    }

    /// <summary>
    /// Reads one CRLF-terminated line as raw bytes, bounded, returning null at end of stream.
    /// </summary>
    /// <remarks>
    /// Buffered, so a message is not read a byte at a time, but the buffer is refilled only when
    /// exhausted, which is what keeps a TLS upgrade from stranding plaintext in it.
    /// </remarks>
    private async ValueTask<byte[]?> ReadLineAsync(
        TimeSpan timeout,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);

        while (true)
        {
            if (_readPosition >= _readLength && !await FillAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                return buffer.Count == 0 ? null : [.. buffer];
            }

            var b = _readBuffer[_readPosition++];

            if (b == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return [.. buffer];
            }

            if (buffer.Count >= maxBytes)
            {
                return null;
            }

            buffer.Add(b);
        }
    }

    /// <summary>Refills the read buffer. Returns false at end of stream, timeout or failure.</summary>
    private async ValueTask<bool> FillAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _readPosition = 0;
        _readLength = 0;

        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        int read;
        try
        {
            read = await _stream.ReadAsync(_readBuffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client that stops mid-command is slow, not necessarily hostile; the bound is what
            // stops it holding the slot forever. Treated as a disconnection.
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        if (read == 0)
        {
            return false;
        }

        _readLength = read;
        return true;
    }

    private async ValueTask ReplyAsync(int code, string text, CancellationToken cancellationToken)
        => await ReplyAsync([$"{code.ToString(CultureInfo.InvariantCulture)} {text}"], cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Writes a reply, given as already-prefixed lines.
    /// </summary>
    /// <remarks>
    /// Every line is sanitised first. Some reply text is built from values the client supplied, a
    /// queue id, a hop count, a reason string, and a CR or LF smuggled into any of those would
    /// terminate this reply and let the following line be read by the client as a fresh response.
    /// </remarks>
    private async ValueTask ReplyAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(Sanitise(line)).Append("\r\n");
        }

        var bytes = Encoding.ASCII.GetBytes(builder.ToString());

        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client has gone. The session loop ends on the next read.
        }
    }

    private static string Sanitise(string line)
    {
        var cleaned = line.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        return cleaned.Length <= 400 ? cleaned : string.Concat(cleaned.AsSpan(0, 400), "...");
    }

    /// <summary>Pulls the address out of <c>MAIL FROM:&lt;a@b&gt;</c>, or null when malformed.</summary>
    private static string? ExtractPath(string argument)
    {
        var colon = argument.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return null;
        }

        var rest = argument[(colon + 1)..].Trim();

        // Parameters after the path ("MAIL FROM:<a@b> SIZE=123") are ignored rather than parsed:
        // nothing here acts on them, and honouring a size the sender chose would be trusting a
        // number that exists to help the sender.
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var path = space < 0 ? rest : rest[..space];

        if (path.Length >= 2 && path[0] == '<' && path[^1] == '>')
        {
            path = path[1..^1];
        }

        return path;
    }

    private static string VerbOf(string command)
    {
        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? command : command[..space];
    }

    private static string ArgumentOf(string command)
    {
        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? string.Empty : command[(space + 1)..].Trim();
    }

    /// <summary>Decodes a SASL PLAIN payload: base64 of NUL user NUL password.</summary>
    private static (string Username, string Password)? DecodeSaslPlain(string base64)
    {
        var decoded = DecodeBase64(base64);
        if (decoded is null)
        {
            return null;
        }

        var parts = decoded.Split('\0');
        return parts.Length >= 3 ? (parts[1], parts[2]) : null;
    }

    private static string? DecodeBase64(string value) => DecodeBase64(Encoding.ASCII.GetBytes(value.Trim()));

    private static string? DecodeBase64(byte[] raw)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()));
        }
        catch (FormatException)
        {
            // A client that sends something that is not base64 has simply failed to authenticate.
            return null;
        }
    }
}
