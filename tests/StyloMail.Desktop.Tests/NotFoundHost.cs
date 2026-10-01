using System.Net;
using System.Net.Sockets;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// A Host that answers every request with 404.
/// </summary>
/// <remarks>
/// <para>
/// This is how a deployment that has not mapped its traffic Hub answers a
/// console: the Host is up, authenticated and perfectly healthy, and the route
/// simply is not there. The feed reads that as
/// <see cref="StyloMail.Desktop.Api.TrafficFeedState.NoFeed"/>, which is a
/// finished answer rather than a fault, so it is the one state a bounded retry
/// must not keep knocking on. That makes it the answer a reconnect sequence can
/// end on, which is what the sequence-level test needs and what no existing
/// double could produce: an unroutable address gives
/// <see cref="StyloMail.Desktop.Api.TrafficFeedState.Unreachable"/>, and a
/// refused key gives <c>Refused</c>, and neither of those is a Host saying it
/// has no feed.
/// </para>
/// <para>
/// Not the harness's Host and not a real one. The port is taken from the OS so
/// two of these can run at once, and nothing is bound outside the loopback
/// interface.
/// </para>
/// </remarks>
internal sealed class NotFoundHost : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    private NotFoundHost(HttpListener listener, Uri address)
    {
        _listener = listener;
        Address = address;
        _serving = ServeAsync();
    }

    /// <summary>The base address a <c>TrafficFeed</c> can be pointed at.</summary>
    public Uri Address { get; }

    /// <summary>
    /// Listens on a free loopback port and answers 404 to everything that
    /// arrives, for as long as the returned host is alive.
    /// </summary>
    public static NotFoundHost Start()
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        return new NotFoundHost(listener, new Uri($"http://127.0.0.1:{port}"));
    }

    /// <summary>
    /// Takes a port from the OS and gives it straight back.
    /// </summary>
    /// <remarks>
    /// Bound for an instant and released, rather than a literal in the test, so
    /// this cannot collide with a developer's Host or with another test's. The
    /// gap between releasing it here and <see cref="HttpListener"/> taking it is
    /// a real race and an acceptable one: the alternative is a fixed port that
    /// fails whenever anything else is running.
    /// </remarks>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                // Stopped rather than failed, and there is no one to tell: the
                // disposal below is what ends this loop in the ordinary case.
                return;
            }

            // The status is the whole answer this double exists to give. No
            // body, because the feed's reader never gets as far as one.
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Close();

        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The listener was closed under a pending GetContextAsync, which is
            // how this is meant to end. Nothing here is worth failing a test on.
        }

        _stopping.Dispose();
    }
}
