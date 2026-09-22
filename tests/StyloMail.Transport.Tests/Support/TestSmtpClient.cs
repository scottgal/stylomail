using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace StyloMail.Transport.Tests.Support;

/// <summary>One parsed SMTP reply, as the client side sees it.</summary>
internal sealed record ClientReply(int Code, IReadOnlyList<string> Lines)
{
    public string Text => string.Join(' ', Lines);

    public bool IsPositive => Code is >= 200 and < 300;

    public bool IsTransientFailure => Code is >= 400 and < 500;

    public bool IsPermanentFailure => Code is >= 500 and < 600;
}

/// <summary>
/// A minimal SMTP client for driving the listener in tests.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> reuse the production reply reader. The point of driving the listener
/// end to end is to check that its framing is what the protocol says, and a client built from the
/// same reader would agree with the server about a mistake they shared. This parses independently,
/// and it is also the only way to assert on a reply <em>prefix</em>, which is how a multi-line EHLO
/// response is checked for the capabilities it advertises.
/// </remarks>
internal sealed class TestSmtpClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private Stream _stream;
    private bool _disposed;

    private TestSmtpClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public bool IsEncrypted { get; private set; }

    public static async Task<TestSmtpClient> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
        return new TestSmtpClient(client);
    }

    public async Task<ClientReply> ReadReplyAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        int? code = null;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The server closed the connection without replying.");

            lines.Add(line);

            if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out var lineCode))
            {
                throw new IOException($"Malformed reply line '{line}'.");
            }

            code ??= lineCode;

            if (line.Length == 3 || line[3] == ' ')
            {
                return new ClientReply(code.Value, lines);
            }
        }
    }

    public async Task<ClientReply> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        await WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a command whose argument carries a credential, so it is never logged.</summary>
    public async Task<ClientReply> SendQuietlyAsync(string command, CancellationToken cancellationToken = default)
        => await SendAsync(command, cancellationToken).ConfigureAwait(false);

    /// <summary>Sends DATA, writes the body, and reads the verdict.</summary>
    public async Task<ClientReply> SendMessageAsync(
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync("DATA", cancellationToken).ConfigureAwait(false);
        if (data.Code != 354)
        {
            return data;
        }

        var body = new List<byte>();

        // Same framing a real client applies: CRLF line endings and dot-stuffing. Applied here
        // independently of the transport's own writer so a bug in one is not mirrored by the other.
        var atLineStart = true;
        foreach (var b in payload)
        {
            if (b is (byte)'\r' or (byte)'\n')
            {
                if (b == (byte)'\r')
                {
                    continue;
                }

                body.Add((byte)'\r');
                body.Add((byte)'\n');
                atLineStart = true;
                continue;
            }

            if (atLineStart && b == (byte)'.')
            {
                body.Add((byte)'.');
            }

            body.Add(b);
            atLineStart = false;
        }

        if (!atLineStart)
        {
            body.Add((byte)'\r');
            body.Add((byte)'\n');
        }

        body.AddRange(".\r\n"u8.ToArray());

        await _stream.WriteAsync(body.ToArray(), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientReply> StartTlsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync("STARTTLS", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 220)
        {
            return reply;
        }

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new System.Net.Security.SslClientAuthenticationOptions { TargetHost = "localhost" },
            cancellationToken).ConfigureAwait(false);

        _stream = ssl;
        IsEncrypted = true;
        return reply;
    }

    public async Task<ClientReply> AuthenticatePlainAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(string.Concat("\0", username, "\0", password)));

        return await SendQuietlyAsync($"AUTH PLAIN {payload}", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private async Task WriteLineAsync(string command, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];

        while (true)
        {
            var read = await _stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString([.. buffer]);
            }

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.ASCII.GetString([.. buffer]);
            }

            buffer.Add(one[0]);
        }
    }
}
