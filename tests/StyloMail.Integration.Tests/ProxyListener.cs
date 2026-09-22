using System.Net;
using System.Net.Sockets;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.Integration.Tests;

/// <summary>
/// Puts a real socket in front of a proxy session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connections are accepted one at a time and run to completion.</b> A test here drives one
/// client, so an accept loop that overlaps sessions would add concurrency the test is not exercising
/// and make a failure harder to read. When a test needs two clients, it starts two listeners.
/// </para>
/// <para>
/// <b>The port is chosen by the operating system.</b> Binding a fixed port makes two runs collide,
/// and a container or a stray process holding it would look like a defect in the proxy.
/// </para>
/// </remarks>
public sealed class ProxyListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> _run;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;

    private ProxyListener(
        TcpListener listener,
        Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)
    {
        _listener = listener;
        _run = run;
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static ProxyListener Start(Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ProxyListener(listener, run);
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient accepted;

            try
            {
                accepted = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // The listener was closed underneath us, which is how disposal ends this loop.
                return;
            }

            using (accepted)
            {
                var stream = accepted.GetStream();
                var description = $"client:127.0.0.1:{(accepted.Client.RemoteEndPoint as IPEndPoint)?.Port}";
                await using var channel = new StreamDuplexChannel(stream, stream, description);

                try
                {
                    await _run(channel, _stopping.Token);
                }
                catch (Exception) when (_stopping.IsCancellationRequested)
                {
                    // A session cut short by disposal is the normal end of a test, not a failure.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Expected on the way out.
        }

        _stopping.Dispose();
    }
}
