using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StyloMail.Transport.Smtp;

namespace StyloMail.Transport.Tests.Support;

/// <summary>Scriptable behaviour for <see cref="FakeSmtpServer"/>.</summary>
internal sealed class FakeSmtpBehaviour
{
    public int GreetingCode { get; set; } = 220;

    public string GreetingText { get; set; } = "fake ESMTP ready";

    /// <summary>When true, the server accepts the connection and says nothing at all.</summary>
    public bool Silent { get; set; }

    public int EhloCode { get; set; } = 250;

    public List<string> Capabilities { get; } = ["SIZE 10485760", "8BITMIME"];

    /// <summary>Adds STARTTLS to the advertised capabilities.</summary>
    public bool AdvertiseStartTls { get; set; }

    /// <summary>The reply to the STARTTLS command itself, so a strip-the-capability attack is expressible.</summary>
    public int StartTlsCode { get; set; } = 220;

    public bool AdvertiseAuth { get; set; }

    public string? AuthUsername { get; set; }

    public string? AuthPassword { get; set; }

    public int MailFromCode { get; set; } = 250;

    public int RcptCode { get; set; } = 250;

    /// <summary>Per-recipient RCPT overrides, so one recipient can be refused while others succeed.</summary>
    public Dictionary<string, int> RcptCodesByRecipient { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drops the connection once this many messages have been completed. Models a session that dies
    /// mid-batch, which the transport must recover from for the recipients still owed an attempt.
    /// </summary>
    public int? DropAfterMessageCount { get; set; }

    public int DataCode { get; set; } = 354;

    public int FinalDataCode { get; set; } = 250;

    public string FinalDataText { get; set; } = "OK id=12345";

    /// <summary>
    /// Accepts the body and then never answers, the lost-acknowledgement case that must surface as
    /// in-doubt rather than as a clean failure.
    /// </summary>
    public bool DropAfterDataTerminator { get; set; }

    /// <summary>
    /// Replies to <c>DATA</c> and then closes the connection immediately, without reading any body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This does not reliably interrupt the body, and a name claiming it does is what made two
    /// lanes argue past each other.</b> The server never reads, so what the client observes depends
    /// entirely on whether its body write completed into the kernel socket buffer before the close
    /// was noticed. Measured on loopback:
    /// </para>
    /// <list type="bullet">
    /// <item><b>≤ 4 KB</b>, the whole message and its terminator fit in the buffer and are written
    /// successfully; the client then fails while awaiting the final reply, so the terminator was
    /// written and the outcome is <b>in-doubt</b>. That is honest: the peer may have received
    /// everything.</item>
    /// <item><b>≥ 64 KB</b>, the buffer overflows, the write itself fails, the terminator was never
    /// written, and the outcome is a plain <b>temporary failure</b>. Nothing could have been
    /// accepted without a terminator.</item>
    /// </list>
    /// <para>
    /// Both outcomes are correct; the threshold is the socket buffer. So a test wanting the
    /// pre-terminator path must use a payload <em>deliberately larger</em> than any plausible buffer
    /// and say why, otherwise it silently starts asserting the in-doubt path instead.
    /// </para>
    /// </remarks>
    public bool CloseAfterDataCommand { get; set; }

    /// <summary>
    /// Accepts the body and its terminator, then waits this long before answering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opens a window in which the message is fully transmitted and unanswered, the only state from
    /// which a <em>cancellation</em> (rather than a connection loss) can land after the terminator.
    /// That is the case where the message may already be accepted and the outcome must be in-doubt.
    /// </para>
    /// <para>
    /// <b>This is a ceiling, not a wait, set it long and the test still runs fast.</b> It only has
    /// to outlast whatever the test is racing: 30 seconds is fine if the drain window closes in
    /// 300ms, because nothing waits for the full delay. Set it just longer than the event it must
    /// outlive and let <c>WaitForMessagesAsync</c> be the synchronisation point, since the message is
    /// recorded strictly <em>before</em> the delay begins. That combination is what makes the
    /// cancellation test deterministic and ~400ms rather than a 10-second sleep, worth preserving,
    /// because a suite with no clock dependence is a property this one is praised for.
    /// </para>
    /// <para>
    /// Technique credit: <c>queue-</c>, who found the fast form while writing the seam test.
    /// </para>
    /// </remarks>
    public TimeSpan? FinalReplyDelay { get; set; }
}

/// <summary>One command line the fake server received, and whether it arrived encrypted.</summary>
internal sealed record RecordedCommand(string Text, bool Encrypted);

/// <summary>A message the fake server received.</summary>
internal sealed record ReceivedMessage(string MailFrom, string Recipient, byte[] WireBody)
{
    /// <summary>The body as a receiving MTA would recover it: unstuffed, with CRLF line endings.</summary>
    public byte[] Recovered => SmtpDataWriter.RecoverBody(WireBody);

    public string RecoveredText => Encoding.UTF8.GetString(Recovered);
}

/// <summary>
/// An in-process SMTP endpoint on loopback, with a real socket and real TLS.
/// </summary>
/// <remarks>
/// A mock at the <c>Stream</c> seam would prove that the state machine calls the methods it calls,
/// which is not the question. The questions are whether a byte written by the writer arrives
/// unchanged, whether a real <c>SslStream</c> handshake succeeds before <c>AUTH</c>, and whether a
/// dropped connection after the terminator is distinguishable from one before it, and none of
/// those survive being mocked out. Loopback costs a few milliseconds and tests the whole path.
///
/// <para>
/// Lines are read as <b>raw bytes</b> rather than through a <c>StreamReader</c>. Decoding to a
/// string and re-encoding would quietly normalise the very bytes the byte-preservation tests exist
/// to compare.
/// </para>
/// </remarks>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> LazyCertificate = new(CreateCertificate);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<Task> _connections = [];
    private readonly Lock _gate = new();
    private readonly List<RecordedCommand> _commands = [];
    private readonly List<ReceivedMessage> _messages = [];
    private int _completedMessages;
    private bool _disposed;

    private FakeSmtpServer(FakeSmtpBehaviour behaviour)
    {
        Behaviour = behaviour;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public FakeSmtpBehaviour Behaviour { get; }

    public int Port { get; }

    /// <summary>The self-signed certificate presented on STARTTLS.</summary>
    public static X509Certificate2 Certificate => LazyCertificate.Value;

    /// <summary>Every command line received, in order, across all connections.</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands.Select(c => c.Text)];
            }
        }
    }

    /// <summary>Every command with its encryption state, used to prove nothing secret went in the clear.</summary>
    public IReadOnlyList<RecordedCommand> RecordedCommands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>Every message body received, in order.</summary>
    public IReadOnlyList<ReceivedMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public static FakeSmtpServer Start(FakeSmtpBehaviour? behaviour = null) => new(behaviour ?? new FakeSmtpBehaviour());

    /// <summary>Waits until at least <paramref name="count"/> messages have been received.</summary>
    public async Task WaitForMessagesAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_messages.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException($"The fake server did not receive {count} message(s) in time.");
    }

    /// <summary>Waits until a command matching <paramref name="predicate"/> has been seen.</summary>
    public async Task WaitForCommandAsync(Func<string, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_commands.Any(c => predicate(c.Text)))
                {
                    return;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("The expected command was never received.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Expected on shutdown.
        }

        Task[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        // Connections are awaited rather than abandoned so a handler is never still writing into a
        // listener the next test has already replaced.
        await Task.WhenAll(connections).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var task = HandleConnectionAsync(client, cancellationToken);
            lock (_gate)
            {
                _connections.Add(task);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = (Stream)client.GetStream();

                if (!Behaviour.Silent)
                {
                    await WriteAsync(stream, $"{Behaviour.GreetingCode} {Behaviour.GreetingText}\r\n", cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Accept and say nothing: the transport must bound this rather than wait forever.
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                await ServeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or AuthenticationException)
            {
                // A test that drops the connection is exercising exactly this path.
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        string mailFrom = string.Empty;
        var recipients = new List<string>();
        var authenticated = false;
        var awaitingAuthPayload = false;

        // reconnects, or it would model an upstream that is down rather than one that lost a session.

        while (!cancellationToken.IsCancellationRequested)
        {
            var raw = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return;
            }

            var line = Encoding.ASCII.GetString(raw);

            if (awaitingAuthPayload)
            {
                awaitingAuthPayload = false;
                Record(line, stream is SslStream);
                var ok = DecodeAuthPayload(line, out var user, out var pass);
                authenticated = ok && user == Behaviour.AuthUsername && pass == Behaviour.AuthPassword;
                await WriteAsync(stream, authenticated ? "235 2.7.0 Authenticated\r\n" : "535 5.7.8 Bad credentials\r\n", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var verb = Verb(line);
            switch (verb)
            {
                case "EHLO":
                    Record(line, stream is SslStream);
                    await WriteAsync(stream, BuildEhlo(Behaviour, stream is SslStream), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "HELO":
                    Record(line, stream is SslStream);
                    await WriteAsync(stream, "250 fake\r\n", cancellationToken).ConfigureAwait(false);
                    break;

                case "STARTTLS":
                    Record(line, stream is SslStream);
                    if (Behaviour.StartTlsCode != 220)
                    {
                        await WriteAsync(stream, $"{Behaviour.StartTlsCode} 5.7.0 STARTTLS refused\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    await WriteAsync(stream, "220 2.0.0 Ready to start TLS\r\n", cancellationToken).ConfigureAwait(false);

                    var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsServerAsync(Certificate, false, System.Security.Authentication.SslProtocols.None, false)
                        .ConfigureAwait(false);
                    stream = ssl;
                    break;

                case "AUTH":
                    Record(line, stream is SslStream);
                    if (!Behaviour.AdvertiseAuth)
                    {
                        await WriteAsync(stream, "503 5.5.1 AUTH not offered\r\n", cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    var mechanism = SecondToken(line);
                    if (string.Equals(mechanism, "PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        var token = ThirdToken(line);
                        if (token.Length == 0)
                        {
                            awaitingAuthPayload = true;
                            await WriteAsync(stream, "334 \r\n", cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        var ok = DecodeAuthPayload(token, out var user, out var pass);
                        authenticated = ok && user == Behaviour.AuthUsername && pass == Behaviour.AuthPassword;
                        await WriteAsync(stream, authenticated ? "235 2.7.0 Authenticated\r\n" : "535 5.7.8 Bad credentials\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    if (string.Equals(mechanism, "LOGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        // Two-step: challenge for the username, then the password.
                        authenticated = await RunLoginChallengeAsync(stream, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await WriteAsync(stream, "504 5.5.4 Unrecognised mechanism\r\n", cancellationToken).ConfigureAwait(false);
                    break;

                case "MAIL":
                    Record(line, stream is SslStream);
                    if (Behaviour.AdvertiseAuth && !authenticated)
                    {
                        await WriteAsync(stream, "530 5.7.0 Authentication required\r\n", cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    mailFrom = ExtractPath(line);
                    recipients.Clear();
                    await WriteAsync(stream, $"{Behaviour.MailFromCode} 2.1.0 Sender ok\r\n", cancellationToken).ConfigureAwait(false);
                    break;

                case "RCPT":
                    Record(line, stream is SslStream);
                    var path = ExtractPath(line);
                    recipients.Add(path);

                    var rcptCode = Behaviour.RcptCodesByRecipient.TryGetValue(path, out var perRecipient)
                        ? perRecipient
                        : Behaviour.RcptCode;

                    await WriteAsync(stream, $"{rcptCode} 2.1.5 Recipient ok\r\n", cancellationToken).ConfigureAwait(false);
                    break;

                case "DATA":
                    Record(line, stream is SslStream);
                    if (Behaviour.DataCode != 354)
                    {
                        await WriteAsync(stream, $"{Behaviour.DataCode} 5.5.1 DATA refused\r\n", cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>\r\n", cancellationToken).ConfigureAwait(false);

                    if (Behaviour.CloseAfterDataCommand)
                    {
                        // The body is accepted and then the connection dies with it still in flight,
                        // so the client's own write fails rather than silently succeeding into a
                        // buffer after the peer has gone. This is the shape that must be told apart
                        // from a lost acknowledgement: nothing was committed here, so a retry cannot
                        // duplicate anything.
                        return;
                    }

                    var body = await ReadDataAsync(stream, cancellationToken).ConfigureAwait(false);

                    lock (_gate)
                    {
                        foreach (var recipient in recipients)
                        {
                            _messages.Add(new ReceivedMessage(mailFrom, recipient, body));
                        }

                        _completedMessages++;
                    }

                    // Server-wide and exact rather than per-connection: the intent is "lose the
                    // session on this one message", not "this upstream drops every Nth message",
                    // which would model an outage rather than a lost session.
                    if (Behaviour.DropAfterMessageCount is int dropAfter && _completedMessages == dropAfter)
                    {
                        // The message was consumed and never acknowledged: a lost session, and the
                        // ambiguity that goes with it.
                        return;
                    }

                    if (Behaviour.DropAfterDataTerminator)
                    {
                        // The body and its terminator were consumed, then the connection dies before
                        // the verdict is sent. Whether the message was accepted is unknowable, by
                        // the client and, deliberately, by this server too. This is the ambiguity the
                        // spec requires us to surface rather than paper over.
                        return;
                    }

                    if (Behaviour.FinalReplyDelay is { } delay)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }

                    await WriteAsync(stream, $"{Behaviour.FinalDataCode} {Behaviour.FinalDataText}\r\n", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "RSET":
                    Record(line, stream is SslStream);
                    mailFrom = string.Empty;
                    recipients.Clear();
                    await WriteAsync(stream, "250 2.0.0 Reset\r\n", cancellationToken).ConfigureAwait(false);
                    break;

                case "QUIT":
                    Record(line, stream is SslStream);
                    await WriteAsync(stream, "221 2.0.0 Bye\r\n", cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    Record(line, stream is SslStream);
                    await WriteAsync(stream, "500 5.5.1 Unrecognised command\r\n", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Runs the two-step AUTH LOGIN challenge and reports whether the credentials matched.</summary>
    private async Task<bool> RunLoginChallengeAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteAsync(stream, "334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Username:")) + "\r\n", cancellationToken)
            .ConfigureAwait(false);

        var userLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (userLine is null)
        {
            return false;
        }

        Record("[auth-username]", stream is SslStream);
        var user = Decode(userLine);

        await WriteAsync(stream, "334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")) + "\r\n", cancellationToken)
            .ConfigureAwait(false);

        var passLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (passLine is null)
        {
            return false;
        }

        Record("[auth-password]", stream is SslStream);
        var pass = Decode(passLine);

        var authenticated = user == Behaviour.AuthUsername && pass == Behaviour.AuthPassword;
        await WriteAsync(stream, authenticated ? "235 2.7.0 Authenticated\r\n" : "535 5.7.8 Bad credentials\r\n", cancellationToken)
            .ConfigureAwait(false);

        return authenticated;
    }

    /// <summary>Reads the DATA body up to the terminating dot, returning the stuffed wire bytes.</summary>
    private static async Task<byte[]> ReadDataAsync(Stream stream, CancellationToken cancellationToken)
    {
        var body = new List<byte>();

        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 1 && line[0] == (byte)'.')
            {
                break;
            }

            body.AddRange(line);
            body.Add((byte)'\r');
            body.Add((byte)'\n');
        }

        return [.. body];
    }

    private static string BuildEhlo(FakeSmtpBehaviour behaviour, bool encrypted)
    {
        var lines = new List<string> { "250-fake greets you" };
        var capabilities = new List<string>(behaviour.Capabilities);

        if (behaviour.AdvertiseStartTls && !encrypted)
        {
            capabilities.Add("STARTTLS");
        }

        if (behaviour.AdvertiseAuth)
        {
            capabilities.Add("AUTH PLAIN LOGIN");
        }

        for (var i = 0; i < capabilities.Count; i++)
        {
            lines.Add(i == capabilities.Count - 1 ? $"250 {capabilities[i]}" : $"250-{capabilities[i]}");
        }

        if (capabilities.Count == 0)
        {
            lines.Add("250 OK");
        }

        return string.Concat(lines.Select(l => l + "\r\n"));
    }

    private static bool DecodeAuthPayload(string base64, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            var parts = decoded.Split('\0');
            if (parts.Length >= 3)
            {
                username = parts[1];
                password = parts[2];
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return false;
    }

    private static string Decode(byte[] raw)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string Verb(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var verb = space < 0 ? line : line[..space];
        return verb.ToUpperInvariant();
    }

    private static string SecondToken(string line)
    {
        var parts = line.Split(' ');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    private static string ThirdToken(string line)
    {
        var parts = line.Split(' ');
        return parts.Length > 2 ? parts[2] : string.Empty;
    }

    /// <summary>Pulls the address out of <c>MAIL FROM:&lt;a@b&gt;</c> or <c>RCPT TO:&lt;a@b&gt;</c>.</summary>
    private static string ExtractPath(string line)
    {
        var open = line.IndexOf('<', StringComparison.Ordinal);
        var close = line.LastIndexOf('>');

        return open >= 0 && close > open ? line[(open + 1)..close] : string.Empty;
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one CRLF-terminated line as raw bytes, with the terminator removed.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte rather than via <c>StreamReader</c>: decoding and re-encoding would normalise
    /// the payload the byte-preservation tests compare.
    /// </remarks>
    private static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : [.. buffer];
            }

            var b = one[0];

            if (b == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return [.. buffer];
            }

            buffer.Add(b);
        }
    }

    private void Record(string command, bool encrypted)
    {
        lock (_gate)
        {
            _commands.Add(new RecordedCommand(command, encrypted));
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Round-tripped through PKCS#12 so the private key is one SslStream can actually use.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
