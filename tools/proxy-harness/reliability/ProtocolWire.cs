using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace StyloMail.ProxyHarness.Reliability;

internal sealed class ProtocolWire(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
        { NewLine = "\r\n", AutoFlush = true };
    public Task Send(string line, CancellationToken token) => _writer.WriteLineAsync(line.AsMemory(), token);
    public async Task<string> Read(CancellationToken token) => await _reader.ReadLineAsync(token)
        ?? throw new EndOfStreamException("Protocol peer closed");
    public async Task<string> Expect(string prefix, CancellationToken token)
    {
        var line = await Read(token);
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Unexpected protocol status (content omitted)");
        return line;
    }
    public void Dispose() { _reader.Dispose(); _writer.Dispose(); }

    public static async Task<SslStream> SecureClient(TcpClient client, X509Certificate2 certificate, CancellationToken token)
    {
        var ssl = new SslStream(client.GetStream(), false,
            (_, peer, _, _) => peer is not null && peer.GetCertHashString() == certificate.GetCertHashString());
        try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, token); return ssl; }
        catch { await ssl.DisposeAsync(); throw; }
    }
}
