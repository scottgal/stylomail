using System.Net.Security;
using System.Text;
using StyloMail.Transport.Smtp;

namespace StyloMail.Transport.Tests.Support;

/// <summary>Opens sessions against the in-process fake server.</summary>
internal static class SmtpTestRig
{
    /// <summary>
    /// Accepts whatever certificate the fake server presents.
    /// </summary>
    /// <remarks>
    /// Production never passes this, see <c>SocketSmtpChannel.ConnectAsync</c>. The fake server's
    /// certificate is self-signed and generated per test run, so there is nothing for the platform
    /// trust store to verify against.
    /// </remarks>
    internal static readonly RemoteCertificateValidationCallback TrustAnyCertificate = (_, _, _, _) => true;

    /// <summary>An upstream pointing at the fake server with TLS off, the loopback case.</summary>
    internal static SmtpUpstream PlainUpstream(FakeSmtpServer server, string? heloName = null) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Tls = SmtpTlsMode.None,
        HeloName = heloName ?? "stylomail.test",
    };

    /// <summary>An upstream that requires TLS, with optional credentials.</summary>
    internal static SmtpUpstream TlsUpstream(
        FakeSmtpServer server,
        SmtpCredentials? credentials = null,
        SmtpTlsMode tls = SmtpTlsMode.Required) => new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Tls = tls,
            Credentials = credentials,
            HeloName = "stylomail.test",
        };

    /// <summary>Opens a session against <paramref name="server"/>.</summary>
    internal static async Task<SmtpSession> OpenAsync(
        FakeSmtpServer server,
        SmtpUpstream? upstream = null,
        SmtpBounds? bounds = null,
        TimeProvider? clock = null,
        SmtpTranscript? transcript = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveUpstream = upstream ?? PlainUpstream(server);
        var effectiveBounds = bounds ?? new SmtpBounds();

        var channel = await SocketSmtpChannel.ConnectAsync(
            effectiveUpstream.Host,
            effectiveUpstream.Port,
            effectiveUpstream.ImplicitTls,
            effectiveBounds,
            clock ?? TimeProvider.System,
            TrustAnyCertificate,
            cancellationToken).ConfigureAwait(false);

        return await SmtpSession.OpenAsync(
            channel,
            effectiveUpstream,
            effectiveBounds,
            clock ?? TimeProvider.System,
            transcript,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A canonical message: CRLF throughout, terminated, with a body that ends in CRLF.</summary>
    internal static byte[] CanonicalMessage(string body = "Hello, this is a test message.") =>
        Encoding.UTF8.GetBytes(
            "From: sender@example.test\r\n"
            + "To: recipient@example.test\r\n"
            + "Subject: A test message\r\n"
            + "Message-ID: <abc123@example.test>\r\n"
            + "Date: Mon, 22 Sep 2026 10:00:00 +0000\r\n"
            + "\r\n"
            + body
            + "\r\n");

    /// <summary>
    /// Splits off the hop marker the ingress prepends, returning it and the remaining bytes.
    /// </summary>
    /// <remarks>
    /// The ingress adds exactly one <c>Received</c> line, and that is the only difference between
    /// what it was handed and what it stores. This is how a test states that precisely instead of
    /// asserting a loose "contains" that would survive an accidental second line.
    /// </remarks>
    internal static (string HopMarker, byte[] Remainder) SplitLeadingHopMarker(byte[] message)
    {
        var separator = message.AsSpan().IndexOf("\r\n"u8);
        Assert.True(separator > 0, "The stored message did not begin with a header line.");

        var marker = Encoding.ASCII.GetString(message, 0, separator);
        return (marker, message[(separator + 2)..]);
    }

    /// <summary>
    /// The same normalisation the transport applies on the wire, used to state a deviation exactly.
    /// </summary>
    internal static byte[] NormaliseToCrLf(ReadOnlySpan<byte> payload)
    {
        var output = new List<byte>(payload.Length + 16);
        var i = 0;

        while (i < payload.Length)
        {
            var b = payload[i];

            if (b is (byte)'\r' or (byte)'\n')
            {
                i += b == (byte)'\r' && i + 1 < payload.Length && payload[i + 1] == (byte)'\n' ? 2 : 1;
                output.Add((byte)'\r');
                output.Add((byte)'\n');
                continue;
            }

            output.Add(b);
            i++;
        }

        return [.. output];
    }
}
