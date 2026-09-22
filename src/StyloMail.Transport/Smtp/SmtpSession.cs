using System.Globalization;
using System.Text;

namespace StyloMail.Transport.Smtp;

/// <summary>
/// One bounded conversation with one upstream.
/// </summary>
/// <remarks>
/// <para>
/// The session owns the protocol state machine and nothing else: it does not know what a queue is,
/// does not decide whether a message should be sent, and does not resolve MX records. It is handed
/// an already-open channel, establishes the session on it, and then runs <c>MAIL FROM</c> /
/// <c>RCPT TO</c> / <c>DATA</c> transactions until it is disposed.
/// </para>
/// <para>
/// <b>Three rules are enforced here rather than left to configuration.</b>
/// </para>
/// <list type="number">
/// <item><b>A downgrade is never silently accepted.</b> If the upstream advertises <c>STARTTLS</c>
/// and then refuses the command, the session fails. Continuing in the clear after that exchange is
/// the exact signature of an active attacker stripping TLS, and no legitimate server advertises a
/// capability it will not honour. This holds even in <see cref="SmtpTlsMode.Opportunistic"/>.</item>
/// <item><b>Capabilities learned before TLS are discarded.</b> RFC 3207 requires a fresh
/// <c>EHLO</c> after the handshake, and the reason is adversarial: a pre-TLS capability list is
/// attacker-writable, so a server could claim <c>AUTH</c> support that the encrypted session does
/// not have. The pre-TLS list is cleared, not merged.</item>
/// <item><b>Credentials never travel unencrypted.</b> The session refuses to attempt <c>AUTH</c>
/// on an unencrypted channel, independently of <see cref="SmtpUpstream.Validate"/>, so a channel
/// that lost its encryption between the two checks still cannot leak a password.</item>
/// </list>
/// </remarks>
internal sealed class SmtpSession : IAsyncDisposable
{
    private const string StartTlsCapability = "STARTTLS";
    private const string AuthCapability = "AUTH";
    private const string SizeCapability = "SIZE";
    private const string PlainMechanism = "PLAIN";
    private const string LoginMechanism = "LOGIN";

    private readonly ISmtpChannel _channel;
    private readonly SmtpUpstream _upstream;
    private readonly SmtpBounds _bounds;
    private readonly SmtpTranscript _transcript;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _dataBuffer = new byte[8192];
    private readonly Dictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Not readonly: a TLS upgrade replaces the stream underneath, and the reader must follow it.
    /// </summary>
    private SmtpReplyReader _reader;

    private bool _broken;
    private bool _disposed;

    private SmtpSession(
        ISmtpChannel channel,
        SmtpUpstream upstream,
        SmtpBounds bounds,
        TimeProvider timeProvider,
        SmtpTranscript transcript)
    {
        _channel = channel;
        _upstream = upstream;
        _bounds = bounds;
        _transcript = transcript;
        _timeProvider = timeProvider;
        _reader = new SmtpReplyReader(channel.Stream, bounds, timeProvider);
    }

    /// <summary>
    /// Establishes a session on an open channel.
    /// </summary>
    /// <remarks>
    /// <b>Ownership of <paramref name="channel"/> transfers to this call</b>, including on failure:
    /// a channel that could not complete a handshake is closed rather than handed back, because its
    /// state is unknown and a half-established SMTP session cannot be resumed.
    /// </remarks>
    /// <exception cref="SmtpSessionException">The upstream would not establish a usable session.</exception>
    internal static async ValueTask<SmtpSession> OpenAsync(
        ISmtpChannel channel,
        SmtpUpstream upstream,
        SmtpBounds bounds,
        TimeProvider timeProvider,
        SmtpTranscript? transcript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(bounds);

        upstream.Validate();
        bounds.Validate();

        var session = new SmtpSession(channel, upstream, bounds, timeProvider, transcript ?? new SmtpTranscript());

        try
        {
            await session.EstablishAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The session's record of what was said. Empty unless a transcript was supplied.</summary>
    internal SmtpTranscript Transcript => _transcript;

    /// <summary>
    /// Whether this session can still be used.
    /// </summary>
    /// <remarks>
    /// A transport fault leaves the conversation unresynchronisable, so the caller must discard the
    /// session rather than send the next recipient on it. This is how the delivery port knows to
    /// reconnect for the recipients that are still owed an attempt.
    /// </remarks>
    internal bool IsUsable => !_broken && !_disposed;

    /// <summary>
    /// Delivers one message to one recipient.
    /// </summary>
    /// <remarks>
    /// <b>One recipient per transaction, deliberately.</b> Batching recipients into a single
    /// <c>RCPT TO</c> list is cheaper, but it makes a per-recipient outcome impossible and it
    /// discloses every recipient to the upstream as a set, which is how a <c>Bcc</c> leaks. The
    /// queue's own model is per-recipient, so this matches it rather than fighting it.
    ///
    /// <para>
    /// Never throws for a delivery failure; failures come back as an outcome. Only cancellation
    /// propagates, because a cancelled send and a refused send are different facts.
    /// </para>
    /// </remarks>
    internal async ValueTask<SmtpTransactionResult> SendAsync(
        string mailFrom,
        string recipient,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        EnsureUsable();

        // A bad envelope address is data, not a programming error: it arrives from a submitter. It
        // is refused locally rather than thrown, so a crafted recipient cannot take out the worker.
        var addressFault = DescribeAddressFault(mailFrom, allowNullSender: true)
            ?? DescribeAddressFault(recipient, allowNullSender: false);

        if (addressFault is not null)
        {
            return SmtpTransactionResult.RefusedLocally(SmtpDeliveryStage.MailFrom, addressFault);
        }

        if (payload.Length > _bounds.MaxMessageBytes)
        {
            return SmtpTransactionResult.RefusedLocally(
                SmtpDeliveryStage.MailFrom,
                $"The message is {payload.Length.ToString(CultureInfo.InvariantCulture)} bytes, over the " +
                $"{_bounds.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}-byte limit for this upstream.");
        }

        // Set immediately before the end-of-data terminator is written. Everything after that point
        // is ambiguous: the upstream may accept the message whether or not we ever hear about it.
        var terminatorStarted = false;

        // Tracked so a failure is attributed to the stage it actually happened at, rather than to
        // whichever stage the handler happened to assume.
        var stage = SmtpDeliveryStage.MailFrom;

        try
        {
            var sizeParameter = _capabilities.ContainsKey(SizeCapability)
                ? string.Concat(" SIZE=", payload.Length.ToString(CultureInfo.InvariantCulture))
                : string.Empty;

            var mailFromReply = await SendCommandAsync(
                $"MAIL FROM:<{mailFrom}>{sizeParameter}",
                _bounds.CommandTimeout,
                SmtpDeliveryStage.MailFrom,
                cancellationToken).ConfigureAwait(false);

            if (!mailFromReply.IsPositive)
            {
                return SmtpTransactionResult.FromRejection(
                    mailFromReply, SmtpDeliveryStage.MailFrom, $"MAIL FROM was refused: {mailFromReply}");
            }

            stage = SmtpDeliveryStage.RcptTo;
            var rcptReply = await SendCommandAsync(
                $"RCPT TO:<{recipient}>",
                _bounds.CommandTimeout,
                SmtpDeliveryStage.RcptTo,
                cancellationToken).ConfigureAwait(false);

            if (!rcptReply.IsPositive)
            {
                return SmtpTransactionResult.FromRejection(
                    rcptReply, SmtpDeliveryStage.RcptTo, $"RCPT TO was refused: {rcptReply}");
            }

            stage = SmtpDeliveryStage.DataCommand;
            var dataReply = await SendCommandAsync(
                "DATA",
                _bounds.CommandTimeout,
                SmtpDeliveryStage.DataCommand,
                cancellationToken).ConfigureAwait(false);

            if (dataReply.Code != 354)
            {
                return dataReply.IsTransientNegative || dataReply.IsPermanentNegative
                    ? SmtpTransactionResult.FromRejection(
                        dataReply, SmtpDeliveryStage.DataCommand, $"DATA was refused: {dataReply}")
                    : SmtpTransactionResult.Failed(
                        SmtpDeliveryStage.DataCommand,
                        $"Expected 354 after DATA but the upstream replied {dataReply}.");
            }

            stage = SmtpDeliveryStage.DataBody;
            await SmtpDataWriter.WriteBodyAsync(_channel.Stream, payload, _dataBuffer, cancellationToken)
                .ConfigureAwait(false);

            terminatorStarted = true;
            stage = SmtpDeliveryStage.DataTerminator;
            await _channel.Stream.WriteAsync(SmtpDataWriter.Terminator, cancellationToken).ConfigureAwait(false);
            await _channel.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            stage = SmtpDeliveryStage.FinalReply;
            var final = await ReadReplyAsync(_bounds.DataTimeout, SmtpDeliveryStage.FinalReply, cancellationToken)
                .ConfigureAwait(false);

            return final.IsPositive
                ? SmtpTransactionResult.Accepted(final)
                : SmtpTransactionResult.FromRejection(
                    final, SmtpDeliveryStage.FinalReply, $"The upstream refused the message after DATA: {final}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _broken = true;

            // Ambiguity does not care why we stopped. If the terminator was already written, the
            // upstream may have accepted the message whether the cancellation came from a shutdown
            // or from the caller's own deadline, and reporting that as a plain cancellation would
            // invite a silent drop. Only a cancellation before the terminator is unambiguously
            // "nothing was sent", which is the case the caller handles.
            if (!terminatorStarted)
            {
                throw;
            }

            return SmtpTransactionResult.InDoubt(
                "The end-of-data terminator was written and the attempt was then cancelled before the " +
                "acknowledgement arrived. The upstream may have accepted this message.");
        }
        catch (Exception ex) when (IsTransportFault(ex))
        {
            _broken = true;

            return terminatorStarted
                ? SmtpTransactionResult.InDoubt(
                    $"The end-of-data terminator was written but the acknowledgement was not received " +
                    $"({ex.Message}). The upstream may have accepted this message.")
                : SmtpTransactionResult.Failed(stage, ex.Message);
        }
    }

    /// <summary>Sends <c>QUIT</c> and closes the channel. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // QUIT is sent before the disposed flag is set, since SendCommandAsync refuses to run on a
        // disposed session, setting it first would make the polite goodbye impossible.
        if (!_broken)
        {
            try
            {
                // Best effort. A failure here says nothing about any message already accepted, so it
                // must not surface as one.
                await SendCommandAsync("QUIT", _bounds.CommandTimeout, SmtpDeliveryStage.Connect, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFault(ex))
            {
                _transcript.RecordFailure(SmtpDeliveryStage.Connect, $"QUIT not acknowledged: {ex.Message}");
            }
        }

        _disposed = true;
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask EstablishAsync(CancellationToken cancellationToken)
    {
        var greeting = await ReadReplyAsync(_bounds.GreetingTimeout, SmtpDeliveryStage.Greeting, cancellationToken)
            .ConfigureAwait(false);

        if (!greeting.IsPositive)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Greeting,
                greeting.IsPermanentNegative,
                $"The upstream refused the connection: {greeting}");
        }

        await EhloAsync(allowHeloFallback: true, cancellationToken).ConfigureAwait(false);

        if (!_upstream.ImplicitTls && _upstream.Tls != SmtpTlsMode.None)
        {
            await StartTlsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_upstream.Tls == SmtpTlsMode.Required && !_channel.IsEncrypted)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.StartTls,
                isPermanent: false,
                $"TLS is required for upstream '{_upstream.PeerLabel()}' but the session is not encrypted. " +
                "Refusing to send.");
        }

        if (_upstream.Credentials is not null)
        {
            await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EhloAsync(bool allowHeloFallback, CancellationToken cancellationToken)
    {
        ValidateHeloName(_upstream.HeloName);

        var ehlo = await SendCommandAsync(
            $"EHLO {_upstream.HeloName}",
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Ehlo,
            cancellationToken).ConfigureAwait(false);

        if (ehlo.IsPositive)
        {
            ParseCapabilities(ehlo);
            return;
        }

        // HELO is the pre-ESMTP fallback. It advertises nothing, no SIZE, no STARTTLS, no AUTH,         // so it is only usable when we need none of those.
        var needsExtensions = _upstream.Tls == SmtpTlsMode.Required || _upstream.Credentials is not null;

        if (!allowHeloFallback || needsExtensions)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Ehlo,
                ehlo.IsPermanentNegative,
                $"The upstream refused EHLO ({ehlo}), which is required to negotiate " +
                (needsExtensions ? "TLS and authentication." : "this session."));
        }

        var helo = await SendCommandAsync(
            $"HELO {_upstream.HeloName}",
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Ehlo,
            cancellationToken).ConfigureAwait(false);

        if (!helo.IsPositive)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Ehlo,
                helo.IsPermanentNegative,
                $"The upstream refused both EHLO and HELO: {helo}");
        }

        _capabilities.Clear();
    }

    private async ValueTask StartTlsAsync(CancellationToken cancellationToken)
    {
        if (_channel.IsEncrypted)
        {
            return;
        }

        if (!_capabilities.ContainsKey(StartTlsCapability))
        {
            if (_upstream.Tls == SmtpTlsMode.Required)
            {
                throw new SmtpSessionException(
                    SmtpDeliveryStage.StartTls,
                    isPermanent: false,
                    $"TLS is required for upstream '{_upstream.PeerLabel()}' but it did not advertise STARTTLS. " +
                    "This is also what a TLS-stripping attacker looks like, so it is retried rather than " +
                    "treated as a permanent configuration fault.");
            }

            return;
        }

        var reply = await SendCommandAsync(
            "STARTTLS",
            _bounds.CommandTimeout,
            SmtpDeliveryStage.StartTls,
            cancellationToken).ConfigureAwait(false);

        if (reply.Code != 220)
        {
            // Advertised and then refused. Never fall back to plaintext here, in any mode.
            throw new SmtpSessionException(
                SmtpDeliveryStage.StartTls,
                isPermanent: false,
                $"The upstream advertised STARTTLS and then refused it ({reply}). Refusing to continue " +
                "in the clear.");
        }

        // Any bytes the reader pulled in beyond the 220 would be plaintext the server should not
        // have sent, and they would be lost when the stream is replaced. Refuse rather than silently
        // drop them, a peer sending data before the handshake is broken or attacking.
        if (_reader.BufferedByteCount > 0)
        {
            throw new SmtpProtocolException(
                "The upstream sent data beyond the STARTTLS acknowledgement, before the TLS handshake. " +
                "Those bytes cannot be attributed to either side of the handshake.");
        }

        await _channel.StartTlsAsync(_upstream.Host, cancellationToken).ConfigureAwait(false);

        // The reader holds the stream it was built with, so an upgrade underneath it would leave us
        // reading TLS records as if they were SMTP replies. Rebinding here is what makes the
        // encrypted session actually encrypted from the client's point of view.
        _reader = new SmtpReplyReader(_channel.Stream, _bounds, _timeProvider);

        // RFC 3207 §4.2: the server MUST discard knowledge from the pre-TLS EHLO, and so must we.
        // Merging would let a pre-handshake response inject capabilities into the authenticated
        // session, including a fake STARTTLS that hid a real downgrade.
        _capabilities.Clear();
        await EhloAsync(allowHeloFallback: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AuthenticateAsync(CancellationToken cancellationToken)
    {
        var credentials = _upstream.Credentials!;

        if (!_channel.IsEncrypted)
        {
            // Belt and braces with SmtpUpstream.Validate: this is the check that still holds if the
            // channel failed to encrypt for a reason the configuration could not see.
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                isPermanent: true,
                "Credentials are configured but the connection is not encrypted. Refusing to transmit them.");
        }

        if (!_capabilities.TryGetValue(AuthCapability, out var authLine))
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                isPermanent: true,
                $"Credentials are configured but upstream '{_upstream.PeerLabel()}' did not advertise AUTH.");
        }

        var mechanisms = authLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (mechanisms.Contains(PlainMechanism, StringComparer.OrdinalIgnoreCase))
        {
            await AuthenticatePlainAsync(credentials, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (mechanisms.Contains(LoginMechanism, StringComparer.OrdinalIgnoreCase))
        {
            await AuthenticateLoginAsync(credentials, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new SmtpSessionException(
            SmtpDeliveryStage.Auth,
            isPermanent: true,
            $"Upstream '{_upstream.PeerLabel()}' advertises AUTH but supports neither PLAIN nor LOGIN " +
            $"(offered: {authLine}).");
    }

    private async ValueTask AuthenticatePlainAsync(SmtpCredentials credentials, CancellationToken cancellationToken)
    {
        // The SASL PLAIN payload is base64 of NUL user NUL password.
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(string.Concat("\0", credentials.Username, "\0", credentials.Password)));

        var reply = await SendCommandAsync(
            $"AUTH PLAIN {payload}",
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Auth,
            cancellationToken,
            credentialBearing: true).ConfigureAwait(false);

        if (reply.Code == 334)
        {
            // Some servers challenge rather than accepting the initial response.
            reply = await SendCommandAsync(
                payload,
                _bounds.CommandTimeout,
                SmtpDeliveryStage.Auth,
                cancellationToken,
                credentialBearing: true).ConfigureAwait(false);
        }

        if (reply.Code != 235)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                reply.IsPermanentNegative,
                $"AUTH PLAIN was refused: {reply}");
        }
    }

    private async ValueTask AuthenticateLoginAsync(SmtpCredentials credentials, CancellationToken cancellationToken)
    {
        var challenge = await SendCommandAsync(
            "AUTH LOGIN",
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Auth,
            cancellationToken).ConfigureAwait(false);

        if (challenge.Code != 334)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                challenge.IsPermanentNegative,
                $"AUTH LOGIN was refused before the username was sent: {challenge}");
        }

        var userReply = await SendCommandAsync(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Username)),
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Auth,
            cancellationToken,
            credentialBearing: true).ConfigureAwait(false);

        if (userReply.Code != 334)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                userReply.IsPermanentNegative,
                $"The upstream did not request a password after the username: {userReply}");
        }

        var passwordReply = await SendCommandAsync(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Password)),
            _bounds.CommandTimeout,
            SmtpDeliveryStage.Auth,
            cancellationToken,
            credentialBearing: true).ConfigureAwait(false);

        if (passwordReply.Code != 235)
        {
            throw new SmtpSessionException(
                SmtpDeliveryStage.Auth,
                passwordReply.IsPermanentNegative,
                $"AUTH LOGIN was refused: {passwordReply}");
        }
    }

    private void ParseCapabilities(SmtpReply ehlo)
    {
        _capabilities.Clear();

        // Line 0 is the server's greeting text, not a capability. Subsequent lines are keyword
        // optionally followed by parameters ("SIZE 10485760", "AUTH PLAIN LOGIN").
        for (var i = 1; i < ehlo.Lines.Count; i++)
        {
            var line = ehlo.Lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var keyword = space < 0 ? line : line[..space];
            var parameters = space < 0 ? string.Empty : line[(space + 1)..].Trim();

            // First wins: a server repeating a keyword is malformed, and taking the last would let a
            // later line rewrite an earlier, already-considered capability.
            _capabilities.TryAdd(keyword, parameters);
        }
    }

    private async ValueTask<SmtpReply> SendCommandAsync(
        string command,
        TimeSpan timeout,
        SmtpDeliveryStage stage,
        CancellationToken cancellationToken,
        bool credentialBearing = false)
    {
        EnsureUsable();

        if (command.Length > _bounds.MaxCommandBytes)
        {
            throw new SmtpProtocolException(
                $"A command exceeded the {_bounds.MaxCommandBytes}-byte limit at stage {stage}.");
        }

        _transcript.RecordClient(command, credentialBearing);

        var line = Encoding.ASCII.GetBytes(string.Concat(command, "\r\n"));

        try
        {
            await _channel.Stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await _channel.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _broken = true;
            throw;
        }
        catch (Exception ex) when (IsTransportFault(ex))
        {
            _broken = true;
            _transcript.RecordFailure(stage, ex.Message);
            throw new SmtpConnectionLostException($"Could not send the command at stage {stage}.", ex);
        }

        return await ReadReplyAsync(timeout, stage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SmtpReply> ReadReplyAsync(
        TimeSpan timeout,
        SmtpDeliveryStage stage,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _reader.ReadReplyAsync(timeout, cancellationToken).ConfigureAwait(false);
            _transcript.RecordServer(reply);
            return reply;
        }
        catch (Exception ex) when (IsTransportFault(ex) || ex is OperationCanceledException)
        {
            // Cancellation counts as breaking the session even though it is not a transport fault:
            // a read abandoned mid-reply leaves the stream positioned inside a response, and a QUIT
            // sent on it would block for the full command timeout waiting for a reply that is being
            // read as the tail of the previous one.
            _broken = true;
            _transcript.RecordFailure(stage, ex.Message);
            throw;
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_broken)
        {
            throw new InvalidOperationException(
                "This session is no longer usable, a transport fault left it in an unknown state. " +
                "Open a new one.");
        }
    }

    private static bool IsTransportFault(Exception ex) =>
        ex is SmtpTimeoutException or SmtpConnectionLostException or SmtpProtocolException or IOException;

    /// <summary>
    /// Describes why an address cannot be used as an SMTP path, or null when it can.
    /// </summary>
    /// <remarks>
    /// <b>This is a command-injection control, not a format check.</b> An address is interpolated
    /// into <c>MAIL FROM:&lt;…&gt;</c>, so an address containing CR or LF terminates the command and
    /// starts a new one, letting a crafted envelope recipient append <c>RCPT TO</c> or <c>DATA</c>
    /// lines of the submitter's choosing. Angle brackets are refused for the same reason: they close
    /// the address early and leave the rest of the line as bare syntax.
    ///
    /// <para>
    /// Non-ASCII is refused rather than transcoded. SMTPUTF8 is not negotiated here, so a mangled
    /// address is worse than a refused one: it would deliver to the wrong mailbox.
    /// </para>
    ///
    /// <para>
    /// Returns a reason instead of throwing because the input is <em>data</em>, it comes from a
    /// submitter and from the queue, not from our own code. A throw here would let one crafted
    /// recipient crash the delivery worker for every other message it was handling.
    /// </para>
    /// </remarks>
    private static string? DescribeAddressFault(string address, bool allowNullSender)
    {
        if (address.Length == 0)
        {
            // The null sender, used for bounces. Renders as MAIL FROM:<>, which is correct.
            return allowNullSender
                ? null
                : "A recipient address must not be empty.";
        }

        foreach (var c in address)
        {
            if (c is '\r' or '\n' or '\0')
            {
                return "The envelope address contains CR, LF or NUL, which would terminate the command " +
                    "line and allow SMTP command injection. Refusing to send.";
            }

            if (c is '<' or '>')
            {
                return "The envelope address contains angle brackets, which truncate the address inside " +
                    "its MAIL FROM:<> / RCPT TO:<> delimiters. Refusing to send.";
            }

            if (c > 127)
            {
                return "The envelope address is not ASCII. SMTPUTF8 is not negotiated, so it would be " +
                    "transcoded into a different mailbox rather than refused. Refusing to send.";
            }
        }

        return null;
    }

    private static void ValidateHeloName(string heloName)
    {
        foreach (var c in heloName)
        {
            if (c is '\r' or '\n' or '\0' or ' ' || c > 127)
            {
                throw new InvalidOperationException(
                    "The EHLO name must be a single printable ASCII token containing no CR, LF, NUL or space.");
            }
        }
    }
}

/// <summary>Small helpers that keep upstream identity out of string interpolation by hand.</summary>
internal static class SmtpUpstreamExtensions
{
    /// <summary>A label safe to put in a diagnostic. Host and port contain no credentials.</summary>
    internal static string PeerLabel(this SmtpUpstream upstream) =>
        $"{upstream.Host}:{upstream.Port.ToString(CultureInfo.InvariantCulture)}";
}
