using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using StyloMail.Host.Traffic;

namespace StyloMail.Host.Tests;

/// <summary>
/// A console's end of the live feed: a real SignalR client over the host's real hub.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real client rather than a double.</b> Everything this lane has to get right lives on the
/// wire: the negotiate handshake, the header on both it and the transport's own request, the
/// tenant group, the JSON envelope and the fact that a notice arrives at all. A fake hub context
/// can assert none of those, and a suite that only had one would be green over a hub nobody could
/// actually connect to.
/// </para>
/// <para>
/// Notices are kept as <see cref="JsonElement"/> rather than deserialised into a type, so a test can
/// assert what was actually sent, including that an enum travelled as a name rather than a number.
/// Deserialising first would make the shape the client's assumption rather than the server's
/// behaviour.
/// </para>
/// </remarks>
internal sealed class TrafficSubscriber : IAsyncDisposable
{
    /// <summary>How long a test waits for a notice before calling the feed dead.</summary>
    /// <remarks>
    /// Generous, because the failure this guards is "nothing ever arrived" and a tight bound would
    /// turn a slow machine into a failing build. It is the <em>absence</em> of an arrival that is
    /// being bounded, and a real regression here is bounded by forever rather than by seconds.
    /// </remarks>
    public static readonly TimeSpan ArrivalWindow = TimeSpan.FromSeconds(15);

    private readonly HubConnection _connection;
    private readonly ConcurrentQueue<JsonElement> _pending = new();
    private readonly List<JsonElement> _received = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Lock _gate = new();

    private TrafficSubscriber(HubConnection connection) => _connection = connection;

    /// <summary>Everything this subscriber has been sent, in order.</summary>
    public IReadOnlyList<JsonElement> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public static async Task<TrafficSubscriber> ConnectAsync(
        TestHost host,
        string apiKey,
        HttpTransportType transport = HttpTransportType.LongPolling)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{TrafficHub.Path}", options =>
            {
                // The host under test, in process. Everything above this line is still the real
                // client talking the real protocol.
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();

                // The header, on the negotiate request and on the transport's own requests. This is
                // the mechanism the console uses, and the reason the key is not in the URL.
                options.Headers.Add(TestPrincipals.ApiKeyHeader, apiKey);

                // Defaults to long polling: the test host serves in process and this transport
                // exercises the whole hub path, negotiate included, without depending on an upgrade
                // the in-memory server may refuse. A test that needs the upgrade asks for it, and
                // that is the one which proves the header survives the handshake.
                options.Transports = transport;

                if (transport == HttpTransportType.WebSockets)
                {
                    // A socket carries the upgrade to a real address, and an in-process host has
                    // none, so the dial is replaced by the test host's own WebSocket client.
                    // Everything above it is the real transport: the SignalR handshake, the
                    // framing, the hub route and the header on the upgrade request, which is the
                    // rule this exercises. ConfigureRequest is where that header goes, and it is
                    // the only thing this substitution decides.
                    var handshake = host.Server.CreateWebSocketClient();
                    handshake.ConfigureRequest = request =>
                        request.Headers[TestPrincipals.ApiKeyHeader] = apiKey;

                    options.WebSocketFactory = async (context, token) =>
                        await handshake.ConnectAsync(context.Uri, token).ConfigureAwait(false);
                }
            })
            .Build();

        var subscriber = new TrafficSubscriber(connection);
        connection.On<JsonElement>(TrafficHub.NoticeMethod, subscriber.Record);

        await connection.StartAsync();

        return subscriber;
    }

    /// <summary>The next notice, waiting for it if it has not arrived yet.</summary>
    /// <remarks>
    /// The failure names what this subscriber actually received before it gave up. A timeout that
    /// only says "nothing arrived" cannot distinguish a lost notice from a lost connection, and both
    /// from a subscription that was never established, so the next reader would have to reproduce
    /// the whole thing to learn what one line here would have told them.
    /// </remarks>
    public async Task<JsonElement> NextAsync()
    {
        if (!await _arrived.WaitAsync(ArrivalWindow).ConfigureAwait(false))
        {
            var heard = Received;
            var described = heard.Count == 0
                ? "nothing at all"
                : string.Join(
                    ", ",
                    heard.Select(n => $"{n.GetProperty("kind").GetString()} {n.GetProperty("subjectId").GetString()}"));

            Assert.Fail(
                $"No notice arrived within {ArrivalWindow.TotalSeconds:F0}s. What this subscriber "
                + $"received before giving up was: {described}.");
        }

        Assert.True(_pending.TryDequeue(out var notice));
        return notice;
    }

    /// <summary>Waits for a moment of quiet, then answers what this subscriber was sent.</summary>
    /// <remarks>
    /// Used only for the negative half of tenant isolation, and only ever after the positive half
    /// has arrived on the same connection, so the wait is not the evidence: the evidence is that a
    /// client which demonstrably receives its own tenant's traffic receives nothing else.
    /// </remarks>
    public async Task<IReadOnlyList<JsonElement>> ReceiveAndSettleAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        return Received;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private void Record(JsonElement notice)
    {
        lock (_gate)
        {
            _received.Add(notice);
        }

        _pending.Enqueue(notice);
        _arrived.Release();
    }
}

/// <summary>Connecting a console to a host under test.</summary>
internal static class TrafficSubscriberExtensions
{
    public static Task<TrafficSubscriber> SubscribeTrafficAsync(
        this TestHost host,
        string apiKey,
        HttpTransportType transport = HttpTransportType.LongPolling)
        => TrafficSubscriber.ConnectAsync(host, apiKey, transport);
}

/// <summary>
/// A hub context that records what was sent, and to which audience.
/// </summary>
/// <remarks>
/// <b>This is the seam's observer, and it is deliberately on the server side of the hub.</b> A
/// recording hub context can say what group a change was addressed to and what was in it, which is
/// the part that decides whether tenant isolation holds; the JSON envelope and the transport are
/// verified against a real client over a real connection in <see cref="TrafficHubTests"/>. Neither
/// half subsumes the other, and one of them is fast enough to be run on every change.
/// </remarks>
internal sealed class RecordingHubContext : IHubContext<TrafficHub>
{
    public List<HubSend> Sends { get; } = [];

    public IHubClients Clients => new RecordingHubClients(Sends);

    public IGroupManager Groups => throw new NotSupportedException(
        "The adapter addresses audiences through Clients; a test double that also offered Groups "
        + "would be able to satisfy code the real adapter is not allowed to reach for.");

    internal void Record(string audience, string method, object?[] args)
        => Sends.Add(new HubSend(audience, method, args));
}

internal sealed record HubSend(string Audience, string Method, object?[] Args);

/// <summary>What a hub that is down says, in both of its shapes.</summary>
internal static class HubIsDown
{
    public static InvalidOperationException Failure() => new(
        "The hub is unavailable (injected by the test suite). This exception reaching the caller "
        + "would mean a mail path depends on the live feed.");
}

/// <summary>
/// A hub context that throws the moment it is touched, which is a disposed context or a transport
/// that has already failed.
/// </summary>
/// <remarks>
/// <b>Throwing is the harsh end of the failure, and the only one worth testing against.</b> A hub
/// with no listeners accepts a send and delivers it to nobody, which is indistinguishable from
/// success at this seam, so the quiet case proves nothing. This double fails synchronously.
/// </remarks>
internal sealed class ThrowingHubContext : IHubContext<TrafficHub>
{
    public IHubClients Clients => throw HubIsDown.Failure();

    public IGroupManager Groups => throw HubIsDown.Failure();
}

/// <summary>
/// A hub context that accepts the send and faults afterwards, which is a dead connection or a
/// serialization fault.
/// </summary>
/// <remarks>
/// The second shape, deliberately: a synchronous throw happens before the awaited call and a faulted
/// task happens after it, and a guard that covered one and not the other would look complete while
/// leaving the other path able to reach a delivery. Both are failures the seam must absorb.
/// </remarks>
internal sealed class FaultingHubContext : IHubContext<TrafficHub>
{
    public IHubClients Clients => new FaultingHubClients();

    public IGroupManager Groups => new FaultingHubClients();
}

internal sealed class FaultingHubClients : IHubClients, IGroupManager, IClientProxy
{
    public IClientProxy All => this;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;

    public IClientProxy Client(string connectionId) => this;

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;

    public IClientProxy Group(string groupName) => this;

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;

    public IClientProxy User(string userId) => this;

    public IClientProxy Users(IReadOnlyList<string> userIds) => this;

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.FromException(HubIsDown.Failure());

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.FromException(HubIsDown.Failure());

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.FromException(HubIsDown.Failure());
}

internal sealed class RecordingHubClients : IHubClients
{
    private readonly List<HubSend> _sends;

    public RecordingHubClients(List<HubSend> sends) => _sends = sends;

    public IClientProxy All => new RecordingClientProxy(_sends, "all");

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)
        => new RecordingClientProxy(_sends, $"all but {excludedConnectionIds.Count}");

    public IClientProxy Client(string connectionId)
        => throw new NotSupportedException(
            "The seam addresses a tenant's group or every connection, never one connection id: a "
            + "change produced by a request has no caller to answer, and a per-connection send would "
            + "mean the emission knew about a particular console.");

    public IClientProxy Clients(IReadOnlyList<string> connectionIds)
        => throw new NotSupportedException("See Client(string).");

    public IClientProxy Group(string groupName) => new RecordingClientProxy(_sends, $"group:{groupName}");

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
        => new RecordingClientProxy(_sends, $"group:{groupName} except {excludedConnectionIds.Count}");

    public IClientProxy Groups(IReadOnlyList<string> groupNames)
        => throw new NotSupportedException(
            "A change belongs to exactly one tenant, or to the host. Sending one change to a set of "
            + "groups is how a change comes to be delivered to a tenant it does not belong to.");

    public IClientProxy User(string userId)
        => throw new NotSupportedException("See Client(string).");

    public IClientProxy Users(IReadOnlyList<string> userIds)
        => throw new NotSupportedException("See Client(string).");
}

internal sealed class RecordingClientProxy : IClientProxy
{
    private readonly List<HubSend> _sends;
    private readonly string _audience;

    public RecordingClientProxy(List<HubSend> sends, string audience)
    {
        _sends = sends;
        _audience = audience;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        _sends.Add(new HubSend(_audience, method, args));
        return Task.CompletedTask;
    }
}
