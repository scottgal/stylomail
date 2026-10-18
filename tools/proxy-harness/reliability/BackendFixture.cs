using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace StyloMail.ProxyHarness.Reliability;

internal enum BackendMode { Healthy, Refused, ConnectStall, NoGreeting }

internal sealed class BackendFixture(X509Certificate2 certificate, bool smtp, BackendMode mode) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private Task? _accept;
    private long _next;
    private int _port;
    public int Port => _port;
    public int Active => _clients.Count;
    public IReadOnlyList<string> Errors => _errors.ToArray();
    public void Start()
    {
        if (mode == BackendMode.ConnectStall)
        {
            _listener.Server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _port = ((IPEndPoint)_listener.Server.LocalEndPoint!).Port;
        }
        else
        {
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (mode == BackendMode.Refused) _listener.Stop();
            else _accept = Accept();
        }
    }
    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            var id = Interlocked.Increment(ref _next);
            _clients[id] = client;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tasks[id] = completion.Task;
            _ = Serve(id, client, completion);
        }
    }
    private async Task Serve(long id, TcpClient client, TaskCompletionSource completion)
    {
        try
        {
            using (client)
            await using (var tls = new SslStream(client.GetStream(), false))
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
                if (mode == BackendMode.NoGreeting)
                {
                    var discard = new byte[1];
                    while (await tls.ReadAsync(discard, _stop.Token) != 0) { }
                    return;
                }
                using var wire = new ProtocolWire(tls);
                if (smtp) await Smtp(wire, _stop.Token); else await Imap(wire, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e) { _errors.Enqueue(e.GetType().Name); }
        finally { _clients.TryRemove(id, out _); _tasks.TryRemove(id, out _); completion.TrySetResult(); }
    }
    private static async Task Imap(ProtocolWire wire, CancellationToken token)
    {
        await wire.Send("* OK fixture", token);
        var auth = await wire.Read(token);
        if (!auth.StartsWith("S1 AUTHENTICATE PLAIN ", StringComparison.Ordinal)) throw new IOException("Unexpected backend authentication framing");
        await wire.Send("S1 OK authenticated", token);
        while (true)
        {
            var line = await wire.Read(token);
            var parts = line.Split(' ', 2);
            if (parts.Length != 2) throw new IOException("Bad fixture command");
            if (parts[1] == "LOGOUT") { await wire.Send("* BYE fixture", token); await wire.Send(parts[0] + " OK logout", token); return; }
            if (parts[1].StartsWith("FETCH", StringComparison.Ordinal))
            {
                const string body = "Subject: fixture\r\n\r\nloopback message\r\n";
                await wire.Send($"* 1 FETCH (BODY[] {{{System.Text.Encoding.UTF8.GetByteCount(body)}}}", token);
                await wire.Send(body + ")", token);
            }
            await wire.Send(parts[0] + " OK complete", token);
        }
    }
    private static async Task Smtp(ProtocolWire wire, CancellationToken token)
    {
        await wire.Send("220 fixture", token);
        await wire.Expect("EHLO ", token);
        await wire.Send("250-fixture", token);
        await wire.Send("250 AUTH PLAIN", token);
        await wire.Expect("AUTH PLAIN ", token);
        await wire.Send("235 authenticated", token);
        while (true)
        {
            var line = await wire.Read(token);
            if (line == "QUIT") { await wire.Send("221 bye", token); return; }
            if (line == "DATA")
            {
                await wire.Send("354 ready", token);
                while (await wire.Read(token) != ".") { }
            }
            await wire.Send("250 accepted", token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _listener.Server.Dispose();
        foreach (var client in _clients.Values) client.Dispose();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }
}
