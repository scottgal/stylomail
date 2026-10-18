using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StyloMail.ProxyHarness.Reliability;

internal static class ProtocolClient
{
    public static async Task<double> Run(ProxyFixture fixture, bool smtp, string fault, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var started = Stopwatch.GetTimestamp();
        await client.ConnectAsync(IPAddress.Loopback, smtp ? fixture.SmtpPort : fixture.ImapPort, token);
        if (smtp)
        {
            using var plain = new ProtocolWire(client.GetStream());
            await plain.Expect("220", token);
            await plain.Send("EHLO fixture", token);
            await plain.Expect("250-", token); await plain.Expect("250 STARTTLS", token);
            await plain.Send("STARTTLS", token); await plain.Expect("220", token);
        }
        await using var tls = await ProtocolWire.SecureClient(client, fixture.Certificate, token);
        using var wire = new ProtocolWire(tls);
        if (smtp)
        {
            await wire.Send("EHLO fixture", token);
            await wire.Expect("250-", token); await wire.Expect("250 AUTH", token);
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + ProxyFixture.Login + "\0" + fixture.ClientPassword));
            await wire.Send("AUTH PLAIN " + auth, token);
            await wire.Expect("235", token);
        }
        else
        {
            await wire.Expect("* OK", token);
            await wire.Send($"a1 LOGIN {ProxyFixture.Login} {fixture.ClientPassword}", token);
            await wire.Expect("a1 OK", token);
        }
        var authenticationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Keep sessions overlapping; the soak must exercise simultaneous live sessions.
        await Task.Delay(25, token);
        if (fault == "half-close")
        {
            client.Client.Shutdown(SocketShutdown.Send);
            try { while (await tls.ReadAsync(new byte[128], token) != 0) { } }
            catch (IOException) { }
            return authenticationMilliseconds;
        }
        if (smtp)
        {
            await wire.Send("MAIL FROM:<sender@example.test>", token); await wire.Expect("250", token);
            await wire.Send("RCPT TO:<receiver@example.test>", token); await wire.Expect("250", token);
            await wire.Send("DATA", token); await wire.Expect("354", token);
            await wire.Send("Subject: loopback fixture", token);
            await wire.Send("", token);
            await wire.Send(new string('x', 1024), token);
            if (fault == "abort-data")
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Dispose();
                return authenticationMilliseconds;
            }
            await wire.Send(".", token); await wire.Expect("250", token);
            await wire.Send("QUIT", token); await wire.Expect("221", token);
        }
        else
        {
            await wire.Send("a2 FETCH 1 BODY[]", token);
            await wire.Expect("* 1 FETCH", token);
            // A nonzero literal control proves the soak traversed the retrieval byte relay.
            await wire.Expect("Subject: fixture", token);
            var sawBody = false;
            while (true)
            {
                var line = await wire.Read(token);
                if (line == "loopback message") sawBody = true;
                if (line.StartsWith("a2 OK", StringComparison.Ordinal)) break;
            }
            if (!sawBody) throw new IOException("Retrieval body control missing");
            await wire.Send("a3 LOGOUT", token);
            await wire.Expect("* BYE", token); await wire.Expect("a3 OK", token);
        }
        return authenticationMilliseconds;
    }
}
