using System.Net;
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
    private readonly TcpAccessProxyListener _listener;

    private ProxyListener(TcpAccessProxyListener listener) => _listener = listener;

    public int Port => _listener.BoundPort;

    public static ProxyListener Start(
        string tenantId,
        Func<IDuplexChannel, CancellationToken, Task<SessionOutcome>> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(run);
        return new ProxyListener(TcpAccessProxyListener.Start(
            IPAddress.Loopback,
            0,
            tenantId,
            (_, channel, cancellationToken) => run(channel, cancellationToken)));
    }

    public ValueTask DisposeAsync() => _listener.DisposeAsync();
}
