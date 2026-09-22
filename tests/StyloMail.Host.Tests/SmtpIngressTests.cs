using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Core;
using StyloMail.Host.Hosting;
using StyloMail.Queue;

namespace StyloMail.Host.Tests;

/// <summary>
/// The SMTP ingress driven the way a real client drives it: over a socket, against the running host.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests talk to the host rather than to a component of it</b>, which is deliberate. The
/// defects this session has been finding have all had the same shape — a seam that is correct on
/// both sides and broken between them — and a seam is only observable from outside both ends. The
/// unit tests above prove what the sink decides; this proves a client on a socket can reach it and
/// that the answer it receives corresponds to a row that exists.
/// </para>
/// <para>
/// The reply the client reads is the acceptance. Everything else about the conversation is setup.
/// </para>
/// </remarks>
public sealed class SmtpIngressTests
{
    [Fact]
    public async Task A_message_from_an_external_client_is_accepted_with_the_queue_id_it_was_given()
    {
        using var host = new TestHost().WithSmtpIngress("example.test");
        using var client = await SmtpClient.ConnectAsync(host.BoundIngressPort);

        await client.ExpectAsync("220");
        await client.SendExpectingAsync("EHLO test.example.com", "250");
        await client.SendExpectingAsync("MAIL FROM:<sender@example.com>", "250");
        await client.SendExpectingAsync("RCPT TO:<recipient@example.test>", "250");
        await client.SendExpectingAsync("DATA", "354");

        var reply = await client.SendBodyAsync(TestMessages.SampleMime);
        await client.SendExpectingAsync("QUIT", "221");

        // A 250 here is the transfer of responsibility: the client is entitled to delete its copy.
        Assert.StartsWith("250", reply, StringComparison.Ordinal);

        // The reply names the durable row it is a claim about, and that row has to be there.
        var queueId = QueueIdFrom(reply);
        Assert.NotNull(queueId);

        var item = await host.Services.GetRequiredService<QueueStore>()
            .GetItemAsync(queueId!, "inbound");

        Assert.NotNull(item);
        Assert.Equal(MailDirection.Inbound, item!.Envelope.Direction);

        // Inbound mail has no principal, so the identity recorded is the boundary's own — never
        // anything the message supplied.
        Assert.StartsWith("smtp:", item.Envelope.TrustedPrincipalId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_external_client_cannot_relay_to_a_domain_this_deployment_does_not_serve()
    {
        // The open-relay refusal, end to end. With no credentials on the connection, the recipient
        // domain is the entire inbound authorisation model — so this is the check standing between
        // the listener and being a public mail injection endpoint.
        using var host = new TestHost().WithSmtpIngress("example.test");
        using var client = await SmtpClient.ConnectAsync(host.BoundIngressPort);

        await client.ExpectAsync("220");
        await client.SendExpectingAsync("EHLO test.example.com", "250");
        await client.SendExpectingAsync("MAIL FROM:<sender@example.com>", "250");

        var refused = await client.SendAsync("RCPT TO:<victim@elsewhere.test>");

        Assert.StartsWith("550", refused, StringComparison.Ordinal);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
    }

    [Fact]
    public async Task A_message_the_deployment_will_not_take_is_deferred_and_the_client_keeps_it()
    {
        // The counterpart of every acceptance test: when responsibility does not transfer, the client
        // must be told so. A 451 leaves the message with the sender, which is always recoverable.
        using var host = new TestHost().WithSmtpIngress("example.test");
        host.Assessor.Action = MailAction.Defer;

        using var client = await SmtpClient.ConnectAsync(host.BoundIngressPort);

        await client.ExpectAsync("220");
        await client.SendExpectingAsync("EHLO test.example.com", "250");
        await client.SendExpectingAsync("MAIL FROM:<sender@example.com>", "250");
        await client.SendExpectingAsync("RCPT TO:<recipient@example.test>", "250");
        await client.SendExpectingAsync("DATA", "354");

        var reply = await client.SendBodyAsync(TestMessages.SampleMime);

        Assert.StartsWith("451", reply, StringComparison.Ordinal);

        var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
        Assert.Empty(counts);
    }

    [Fact]
    public async Task Shutting_down_with_a_client_still_attached_does_not_fail()
    {
        // A real deployment restarts while clients are mid-conversation, so shutdown with a live
        // session is the ordinary case rather than an edge one.
        //
        // This test is here because that path was broken and the whole suite was green over it: the
        // failure was an ObjectDisposedException thrown out of a session's own cleanup during host
        // shutdown, and it reproduced roughly once in ten runs. A test that only sometimes fails is
        // worse than no test, so the point of this one is to make the sequence — session open, host
        // stopping — happen every time rather than by luck.
        var host = new TestHost().WithSmtpIngress("example.test");
        var client = await SmtpClient.ConnectAsync(host.BoundIngressPort);

        await client.ExpectAsync("220");
        await client.SendExpectingAsync("EHLO test.example.com", "250");
        await client.SendExpectingAsync("MAIL FROM:<sender@example.com>", "250");

        // Deliberately left mid-transaction and still connected: the socket is open and the session
        // is parked reading the next command, so it is genuinely in flight when the host goes away.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        stopwatch.Stop();

        client.Dispose();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Shutdown with a client attached took {stopwatch.Elapsed.TotalSeconds:F1}s; the listener "
            + "is not releasing a session that is parked waiting for a command it will never send.");
    }

    [Fact]
    public void The_listener_is_not_started_unless_the_deployment_asks_for_it()
    {
        // Default off is the safe default for a component that sits on a mail boundary: an
        // unconfigured deployment runs the HTTP host and opens no mail port at all.
        using var host = new TestHost();

        Assert.Null(host.Services.GetRequiredService<SmtpIngressHostedService>().BoundPort);
        Assert.False(host.Services.GetRequiredService<SmtpIngressHostedService>().IsRunning);
    }

    [Fact]
    public void Enabling_the_listener_with_encryption_required_and_no_certificate_refuses_to_start()
    {
        // Not a bug, and worth pinning. With encryption required there is no STARTTLS and therefore
        // no AUTH, so no submission could ever be accepted — and a listener that answered 530 to
        // every client would look like a working service that rejects all mail. Refusing to start is
        // the only answer that cannot be mistaken for healthy.
        var host = new TestHost().WithSmtpIngress("example.test");
        host.Configure("StyloMail:Transport:SmtpIngress:RequireEncryption", "true");

        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<SmtpIngressHostedService>());

            Assert.Contains("STARTTLS", Describe(failure), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                host.Dispose();
            }
            catch (Exception)
            {
                // The host failed to start; disposing a half-built host is best effort.
            }
        }
    }

    /// <summary>Pulls the queue id out of the reply the client was actually handed.</summary>
    private static string? QueueIdFrom(string reply)
    {
        const string marker = "queued as ";
        var at = reply.IndexOf(marker, StringComparison.Ordinal);

        return at < 0 ? null : reply[(at + marker.Length)..].Trim();
    }

    private static string Describe(Exception exception)
    {
        var text = new StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    /// <summary>
    /// A minimal SMTP client. Deliberately hand-written rather than a library: these tests are about
    /// what is on the wire from an ordinary client, and a library's own conventions would be one
    /// more thing between the assertion and the bytes.
    /// </summary>
    private sealed class SmtpClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private SmtpClient(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
        }

        public static async Task<SmtpClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            return new SmtpClient(client);
        }

        /// <summary>Writes a command and returns the reply it produced.</summary>
        /// <remarks>
        /// Returning rather than asserting, so a test that expects a refusal can look at what came
        /// back instead of having to have predicted it before sending.
        /// </remarks>
        public async Task<string> SendAsync(string command)
        {
            await _writer.WriteLineAsync(command);
            return await ReadReplyAsync();
        }

        /// <summary>Writes a command and asserts the reply's code.</summary>
        public async Task<string> SendExpectingAsync(string command, string code)
        {
            var reply = await SendAsync(command);
            Assert.StartsWith(code, reply, StringComparison.Ordinal);
            return reply;
        }

        /// <summary>Writes a message body and its terminator, returning the reply to the end of data.</summary>
        public async Task<string> SendBodyAsync(string message)
        {
            var normalised = message.Replace("\r\n", "\n").Replace("\n", "\r\n");

            foreach (var line in normalised.Split("\r\n"))
            {
                // Dot-stuffing, as a real client does: a line consisting of a single dot would
                // otherwise be read as the end of the message.
                await _writer.WriteLineAsync(line.StartsWith('.') ? "." + line : line);
            }

            await _writer.WriteLineAsync(".");
            return await ReadReplyAsync();
        }

        public async Task ExpectAsync(string code)
        {
            var reply = await ReadReplyAsync();
            Assert.StartsWith(code, reply, StringComparison.Ordinal);
        }

        /// <summary>Reads one complete reply, following continuation lines to the last one.</summary>
        private async Task<string> ReadReplyAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                var line = await _reader.ReadLineAsync();

                if (line is null)
                {
                    throw new InvalidOperationException("The server closed the connection mid-reply.");
                }

                // "250-Line" continues; "250 Line" is the last line of the reply.
                if (line.Length < 4 || line[3] != '-')
                {
                    return line;
                }
            }

            throw new InvalidOperationException("Reply never terminated.");
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _client.Dispose();
        }
    }
}
