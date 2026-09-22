using System.Net;
using System.Net.Sockets;

namespace StyloMail.Transport.Tests.Support;

/// <summary>
/// A loopback endpoint that accepts every connection and immediately closes it, for the whole
/// lifetime of the test.
/// </summary>
/// <remarks>
/// <b>Replaces a "pick a free port and hope it stays free" helper.</b> That version started a probe
/// listener, read its port, stopped it, and returned the number — so between the release and the
/// dial the port belonged to nobody, and any other test in the assembly could take it. If one did,
/// the connection would be <em>accepted</em> and a test asserting that an unreachable upstream
/// yields outcomes would quietly be exercising something else. Low probability, and that is the
/// point: it cannot fire today, which is exactly why it would fire the day the test changed.
///
/// <para>
/// Owning the endpoint for the test's duration removes the window rather than narrowing it. The
/// client's connection still fails at session setup — the greeting read sees a closed socket — so
/// the behaviour under test is unchanged; it simply no longer depends on nobody else wanting the
/// port.
/// </para>
/// <para>
/// The alternative fix, using a reserved documentation address such as TEST-NET-1, does <b>not</b>
/// transfer here and was correctly not offered: it blackholes, so the client would time out rather
/// than fail, which is a different outcome and a much slower test. Finding that out was worth more
/// than the fix.
/// </para>
/// </remarks>
internal sealed class DeadEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private DeadEndpoint(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        _acceptLoop = AcceptAndCloseAsync(_cts.Token);
    }

    /// <summary>The port this endpoint owns for the duration of the test.</summary>
    public int Port { get; }

    /// <summary>
    /// How many connections have been accepted and closed.
    /// </summary>
    /// <remarks>
    /// Exposed so the test can assert the connection was <em>established</em> before failing. Without
    /// it, "accepted then closed" and "refused at connect" produce an identical outcome and an
    /// identical assertion, so the two tests would look interchangeable while claiming to cover
    /// different branches. This is what makes that claim checkable rather than asserted.
    /// </remarks>
    public int AcceptedCount => Volatile.Read(ref _accepted);

    private int _accepted;

    public static DeadEndpoint Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return new DeadEndpoint(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
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
            // Expected on teardown.
        }

        _cts.Dispose();
    }

    private async Task AcceptAndCloseAsync(CancellationToken cancellationToken)
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

            Interlocked.Increment(ref _accepted);

            // Closed without reading, and without a greeting — the peer's session setup fails at the
            // first read, which is the failure this endpoint exists to produce deterministically.
            client.Dispose();
        }
    }
}
