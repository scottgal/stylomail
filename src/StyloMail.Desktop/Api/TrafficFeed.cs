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
    /// <para>
    /// The state that carries the honesty requirement. A feed that stops
    /// delivering looks exactly like a quiet system unless the console says
    /// otherwise, so this is what makes the difference between a console that
    /// is live and one that is showing a photograph of a live one.
    /// </para>
    /// <para>
    /// Not the only state that can follow a live feed. A connect an operator
    /// asks for is a fresh attempt, so what it settles in is whatever the Host
    /// answered with (<see cref="Unreachable"/>, <see cref="Refused"/>), and
    /// what makes those honest after a live feed is
    /// <see cref="TrafficFeed.SurfaceMayBeStale"/> rather than this state.
    /// </para>
    /// </remarks>
    Dropped,
}

/// <summary>
/// How far into a retry sequence the console is, for the operator to read.
/// </summary>
/// <param name="Retry">
/// Which retry, counting from one. The click's own attempt is not one of them:
/// these are what follow it.
/// </param>
/// <param name="Retries">How many retries the cadence allows before giving up.</param>
/// <param name="Waiting">
/// The wait before this retry, or null once the attempt itself is under way.
/// The distinction is the difference between "in 10 seconds" and "now", which is
/// the only thing the operator can act on.
/// </param>
public readonly record struct FeedRetry(int Retry, int Retries, TimeSpan? Waiting);

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
    private readonly TrafficRetryPolicy _retry;
    private HubConnection? _connection;
    private CancellationTokenSource? _sequence;
    private FeedRetry? _retrying;

    /// <summary>Builds a subscription to one Host's feed.</summary>
    /// <param name="hostAddress">The deployment this console is pointed at.</param>
    /// <param name="apiKeys">Where the key comes from. Read once per attempt and never held.</param>
    /// <param name="retryPolicy">
    /// The cadence both paths use. Injected rather than fixed so a test can run a
    /// whole sequence in milliseconds; production passes <see cref="TrafficRetryPolicy.Shared"/>.
    /// </param>
    /// <param name="surfaceMayBeStale">
    /// Whether what is on screen is already known to be behind, for a subscription
    /// that replaces one which had been live. See <see cref="SurfaceMayBeStale"/>.
    /// </param>
    public TrafficFeed(
        Uri hostAddress,
        IApiKeyProvider apiKeys,
        TrafficRetryPolicy? retryPolicy = null,
        bool surfaceMayBeStale = false)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);
        ArgumentNullException.ThrowIfNull(apiKeys);

        _hostAddress = hostAddress;
        _keys = apiKeys;
        _retry = retryPolicy ?? TrafficRetryPolicy.Shared;
        SurfaceMayBeStale = surfaceMayBeStale;
    }

    /// <summary>Where the feed stands right now.</summary>
    public TrafficFeedState State { get; private set; } = TrafficFeedState.NotStarted;

    /// <summary>
    /// Whether what is on screen may now be out of date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by any transition out of <see cref="TrafficFeedState.Live"/>, and
    /// cleared only by <see cref="SurfaceIsCurrent"/>. It is deliberately not
    /// "not currently connected": a deployment with no feed has never been live
    /// and is perfectly up to date, because nothing on screen was ever claimed
    /// to be following it.
    /// </para>
    /// <para>
    /// <b>The fact it carries is about the screen, and the screen outlives a
    /// subscription.</b> The window replaces this object when the operator
    /// reconnects, so it can be told what the last one knew; without that, a
    /// reconnect during an outage would silently drop a warning that took an
    /// outage to raise, and the console would be claiming a currency it does not
    /// have. It is one flag rather than one per state because the question is
    /// the same one: has the host told us something the screen has not heard.
    /// </para>
    /// </remarks>
    public bool SurfaceMayBeStale { get; private set; }

    /// <summary>
    /// The retry in flight, or null when the console is not retrying.
    /// </summary>
    /// <remarks>
    /// Published rather than counted by the caller, because the sequence is the
    /// only thing that knows which attempt it is on, and the operator is owed
    /// that number while it runs. Null means "not trying", which after a
    /// sequence ends is the same as "gave up".
    /// </remarks>
    public FeedRetry? Retrying => _retrying;

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
            // than showing a spinner forever. The cadence is the shared policy
            // and not the library's default, so this path and the operator's
            // (StartWithRetryAsync) cannot drift apart.
            .WithAutomaticReconnect(_retry)
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

    /// <summary>
    /// Opens the feed the way an operator's Reconnect does: one attempt now,
    /// then the same bounded cadence the automatic path uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the attempt loop SignalR does not provide.</b>
    /// <c>WithAutomaticReconnect</c> applies only to a connection that started
    /// successfully, so a Host that is away when the operator asks for it back
    /// gets exactly one try, and the console then settles in
    /// <see cref="TrafficFeedState.Unreachable"/> with no attempt left to make.
    /// The sequence below is bounded by <see cref="TrafficRetryPolicy.Budget"/>
    /// (about 42 seconds on the shared policy) and stops there.
    /// </para>
    /// <para>
    /// Three properties are deliberate, and each is something the operator is
    /// shown or spared:
    /// <list type="bullet">
    /// <item>nothing is attempted without this call, so no console retries on
    /// its own initiative and no sequence starts behind a person's back;</item>
    /// <item>a call while a sequence is already running does nothing, so a
    /// second press cannot restart the countdown or race a second sequence onto
    /// one connection;</item>
    /// <item>the retries are on the shared policy rather than a second copy of
    /// its numbers, so "how long until the console gives up" has one answer.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>An answer the deployment has already given is not retried.</b> A 404
    /// means the deployment has no feed to open and a refusal means this
    /// console's key or privilege will not be accepted; neither changes inside
    /// the budget, and re-presenting a key the Host has just rejected for forty
    /// seconds is a worse failure than the one being retried.
    /// </para>
    /// </remarks>
    public async Task<TrafficFeedState> StartWithRetryAsync(CancellationToken cancellationToken = default)
    {
        if (_sequence is not null)
        {
            return State;
        }

        using var sequence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sequence = sequence;

        try
        {
            var state = await StartAsync(sequence.Token).ConfigureAwait(false);

            for (var retry = 1; state is not TrafficFeedState.Live && CouldChange(state); retry++)
            {
                if (_retry.DelayBeforeRetry(retry) is not { } wait)
                {
                    // The cadence is spent. What is left on screen is the last
                    // attempt's answer, and the stale flag says whether it can
                    // be trusted; this returns rather than throwing, on the same
                    // reasoning as StartAsync.
                    break;
                }

                SetRetrying(new FeedRetry(retry, _retry.RetryLimit, wait));
                await Task.Delay(wait, sequence.Token).ConfigureAwait(false);

                SetRetrying(new FeedRetry(retry, _retry.RetryLimit, Waiting: null));
                state = await StartAsync(sequence.Token).ConfigureAwait(false);
            }

            return state;
        }
        catch (OperationCanceledException)
        {
            // Superseded or disposed. Not a failure: the console has moved on to
            // another Host or stopped, and whoever cancelled owns what happens
            // next.
            return State;
        }
        finally
        {
            _sequence = null;
            SetRetrying(null);
        }
    }

    /// <summary>Whether another attempt could produce a different answer.</summary>
    private static bool CouldChange(TrafficFeedState state)
        => state is not (TrafficFeedState.NoFeed or TrafficFeedState.Refused);

    /// <summary>Publishes the retry in flight, if it changed.</summary>
    private void SetRetrying(FeedRetry? retrying)
    {
        if (_retrying == retrying)
        {
            return;
        }

        _retrying = retrying;
        StateChanged?.Invoke();
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
        // A sequence outlives the frame that started it. A reconnect that
        // supersedes one must not leave the old subscription still knocking for
        // the rest of its budget at a Host the console has already left, and the
        // window unsubscribes before disposing, so nothing is published here.
        _sequence?.Cancel();

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
