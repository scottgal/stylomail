using System.Net.Sockets;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// Opens a real TCP connection to the container, so the backend side of the proxy is exercised over
/// a socket rather than through an in-memory pipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half the in-memory suite cannot reach.</b> <c>ImapBackendConnector</c> and
/// <c>Pop3BackendConnector</c> hand-write the client side of two dialects: the greeting, the
/// <c>AUTHENTICATE</c> or <c>LOGIN</c> exchange, the continuation lines, and the reply parsing.
/// Against <c>FakeBackendTransport</c> they are talking to a script this project wrote, so a
/// misreading of the real grammar is invisible. Pointed at a real server that was written by someone
/// else, it stops being invisible.
/// </para>
/// <para>
/// <b>One connection per session.</b> The proxy opens a fresh backend connection for each client
/// session and never reuses one, which is what this mirrors: no pooling, no keep-alive.
/// </para>
/// </remarks>
internal sealed class TcpBackendTransport : IBackendTransport
{
    private readonly string _host;
    private readonly int _port;

    internal TcpBackendTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async ValueTask<IDuplexChannel> OpenAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();

        // Disposing the channel disposes the network stream, which closes the socket. The TcpClient
        // wrapper goes with it; there is nothing left holding the connection open.
        return new StreamDuplexChannel(stream, stream, $"backend:{_host}:{_port}");
    }
}
