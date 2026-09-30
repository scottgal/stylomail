using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using StyloMail.Desktop.Api.Contracts;
using StyloMail.Desktop.Services;

namespace StyloMail.Desktop.Api;

/// <summary>
/// Where this console stands with respect to a deployment's live feed.
/// </summary>
/// <remarks>
/// Separate states rather than one "not live", because they have different
/// remedies and the remedy is the only reason to show this at all. "This
/// deployment offers no feed" is a finished answer; "the feed stopped" means
/// what is on screen may now be wrong; "the Host refused the key" is a
/// different problem again.
/// </remarks>
public enum TrafficFeedState
{
    /// <summary>No attempt has been made yet.</summary>
    NotStarted,

    /// <summary>An attempt is in flight.</summary>
    Connecting,

    /// <summary>Connected, and notices are arriving.</summary>
    Live,

    /// <summary>The deployment does not offer a feed. Its route is absent.</summary>
    NoFeed,

    /// <summary>The Host refused the console's key, or the review privilege.</summary>
    Refused,

    /// <summary>Nothing answered at the Hub's address.</summary>
    Unreachable,

    /// <summary>
    /// The feed was connected and is not any more.
    /// </summary>
    /// <remarks>
    /// The state that carries the honesty requirement. A feed that stops
    /// delivering looks exactly like a quiet system unless the console says
    /// otherwise, so this is what makes the difference between a console that
    /// is live and one that is showing a photograph of a live one.
    /// </remarks>
    Dropped,
}

/// <summary>
/// The console's subscription to the Host's live-traffic Hub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here arrives as a hint and is re-read over HTTP.</b> The Hub
/// announces that a row moved; nothing in this class decides what the row now
/// says. That is the console's own documented rule for pushed events and it is
/// enforced by what this type exposes: a
/// <see cref="TrafficNotice"/> carries a kind and an id and no state, so a
/// caller that rendered this feed directly would have nothing to render.
/// </para>
/// <para>
/// <b>The key goes in a header on both the negotiate request and the
/// handshake, and there is no path in this file that could put it in a URL.</b>
/// SignalR's usual <c>AccessTokenProvider</c> appends the token to the query
/// string, where it lands in access logs, proxies and crash reports; it is not
/// used here at all, and <see cref="HttpConnectionOptions.Headers"/> is, which
/// is what the Host documents as the console's transport. The Host reads no
/// token from the URL either, so there is nothing on the other side to tempt
/// anyone into it.
/// </para>
/// <para>
/// <b>A console with no key does not open a connection at all</b>, on the same
/// reasoning as the API client: sending an unauthenticated request to discover
/// that we had nothing to authenticate with tells the operator nothing.
/// </para>
/// <para>
/// Events are raised on whatever thread SignalR is using, which is not the UI
/// thread. Marshalling is the window's job (see the rule in MainWindow), not
/// this type's: a class that owned a dispatcher could not be tested without
/// one.
/// </para>
/// </remarks>
public sealed class TrafficFeed : IAsyncDisposable
{
    /// <summary>Where the Hub is mapped. Mirrors <c>StyloMail.Host.Traffic.TrafficHub.Path</c>.</summary>
    public const string HubPath = "/v1/traffic";

    /// <summary>The single method a notice arrives on. Mirrors <c>TrafficHub.NoticeMethod</c>.</summary>
    public const string NoticeMethod = "traffic";

    private readonly Uri _hostAddress;
    private readonly IApiKeyProvider _keys;
    private HubConnection? _connection;

    public TrafficFeed(Uri hostAddress, IApiKeyProvider apiKeys)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);
        ArgumentNullException.ThrowIfNull(apiKeys);

        _hostAddress = hostAddress;
        _keys = apiKeys;
    }

    /// <summary>Where the feed stands right now.</summary>
    public TrafficFeedState State { get; private set; } = TrafficFeedState.NotStarted;

    /// <summary>
    /// Whether what is on screen may now be out of date.
    /// </summary>
    /// <remarks>
    /// True once a feed that had been live stopped, and cleared only when the
    /// surface is read again. It is deliberately not "not currently
    /// connected": a deployment with no feed has never been live and is
    /// perfectly up to date, because nothing on screen was ever claimed to be
    /// following it.
    /// </remarks>
    public bool SurfaceMayBeStale { get; private set; }

    /// <summary>A notice arrived. Raised on a transport thread.</summary>
    public event Action<TrafficNotice>? NoticeReceived;

    /// <summary>
    /// The feed is connected again after a gap, so the visible surface must be read again.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="StateChanged"/> because the Host tells the
    /// console nothing about what was missed: the only honest response to a
    /// reconnect is to read what is on screen afresh.
    /// </remarks>
    public event Action? Resynchronise;

    /// <summary>The state changed. Raised on a transport thread.</summary>
    public event Action? StateChanged;

    /// <summary>The Hub's address for a Host: its scheme, host and port, and the Hub's path.</summary>
    /// <remarks>
    /// A pure function so the derivation is testable, and so a caller can ask
    /// where the feed would be without opening one. Any query or fragment on
    /// the Host address is dropped rather than carried: this is the one place a
    /// query string could smuggle something into a URL, and the Hub takes
    /// nothing that way.
    /// </remarks>
    public static Uri HubAddress(Uri hostAddress)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);

        return new UriBuilder(hostAddress)
        {
            Path = HubPath,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    /// <summary>
    /// Opens the feed. Returns the state it settled in, which is not an exception.
    /// </summary>
    /// <remarks>
    /// <b>A feed that cannot be opened is not a failure of the console.</b>
    /// Every state below <see cref="TrafficFeedState.Live"/> leaves a working
    /// console that reads its rows on demand, which is what this console did
    /// before the Hub existed. So this method reports rather than throws, and
    /// the caller renders the reason.
    /// </remarks>
    public async Task<TrafficFeedState> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return State;
        }

        // The same transport policy the connection screen applies, applied here
        // as well because this is the one address the console opens a
        // long-lived authenticated socket to. Refusing here rather than in the
        // caller keeps the rule next to the connection it governs.
        if (!HostAddressPolicy.IsAcceptable(_hostAddress, out _))
        {
            SetState(TrafficFeedState.Refused);
            return State;
        }

        // One read per connection attempt, and the value goes into a header and
        // nowhere else. No key means no connection: see the type's remarks.
        var key = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            SetState(TrafficFeedState.Refused);
            return State;
        }

        SetState(TrafficFeedState.Connecting);

        var connection = new HubConnectionBuilder()
            .WithUrl(HubAddress(_hostAddress), options =>
            {
                // The header, on negotiate and on the handshake. Never
                // AccessTokenProvider: that is the one that writes the key into
                // the URL, and the Host documents it as refused.
                options.Headers[StyloMailApiClient.ApiKeyHeaderName] = key;

                // Explicit rather than defaulted, since the default set is
                // chosen for browsers. A desktop console can hold a socket
                // open, and long polling would restate the key on every poll
                // for a stream it does not need.
                options.Transports = HttpTransportType.WebSockets;
            })
            // Retries that heal a blip without the operator seeing anything,
            // and give up eventually so the console can say it stopped rather
            // than showing a spinner forever.
            .WithAutomaticReconnect()
            .Build();

        connection.On<TrafficNotice>(NoticeMethod, notice =>
        {
            // Never null: the Hub sends an object, and a null here would be a
            // payload this build cannot read at all. Raising it lets the window
            // decide, which is where the rule about re-reading lives.
            if (notice is not null)
            {
                NoticeReceived?.Invoke(notice);
            }
        });

        connection.Reconnecting += _ =>
        {
            // The gap begins here. The screen is now a photograph.
            SurfaceMayBeStale = true;
            SetState(TrafficFeedState.Dropped);
            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            // The feed is back, but what was missed is unknown: the Host tells
            // us nothing about the gap. Only a fresh read of the visible
            // surface can make the screen true again, and only the window
            // knows what is visible.
            SetState(TrafficFeedState.Live);
            Resynchronise?.Invoke();
            return Task.CompletedTask;
        };

        connection.Closed += _ =>
        {
            // Automatic reconnect has given up. The gap does not close here.
            SurfaceMayBeStale = true;
            SetState(TrafficFeedState.Dropped);
            return Task.CompletedTask;
        };

        _connection = connection;

        try
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            _connection = null;
            SetState(Classify(failure));
            return State;
        }

        SetState(TrafficFeedState.Live);
        return State;
    }

    /// <summary>Records that the visible surface has been read again.</summary>
    /// <remarks>
    /// Called by the window after a successful resynchronisation, so the stale
    /// marker comes off only when the screen is true again, and never merely
    /// because the socket reconnected.
    /// </remarks>
    public void SurfaceIsCurrent() => SurfaceMayBeStale = false;

    public async ValueTask DisposeAsync()
    {
        var connection = _connection;
        _connection = null;

        if (connection is not null)
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task DisposeConnectionAsync(HubConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal of a connection that never opened, or that is already
            // broken, is not a failure worth reporting: there is nothing left
            // to close and nothing to tell the operator.
        }
    }

    /// <summary>
    /// What the Host's answer means, from the exception the client raised.
    /// </summary>
    /// <remarks>
    /// <b>404 is the whole reason this is classified rather than collapsed.</b>
    /// The Host maps the Hub only when the deployment has enabled it, so an
    /// absent route is a deployment saying "no feed", which is a complete
    /// answer and not a fault. Reading it as "unreachable" would send an
    /// operator to check a Host that is answering them perfectly well.
    /// </remarks>
    private static TrafficFeedState Classify(Exception failure)
    {
        for (var candidate = failure; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is HttpRequestException http)
            {
                return http.StatusCode switch
                {
                    HttpStatusCode.NotFound => TrafficFeedState.NoFeed,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TrafficFeedState.Refused,
                    _ => TrafficFeedState.Unreachable,
                };
            }
        }

        // A failure that never reached a status code is one that never got an
        // answer, which is the honest reading for a socket that would not open.
        return TrafficFeedState.Unreachable;
    }

    private void SetState(TrafficFeedState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke();
    }
}
