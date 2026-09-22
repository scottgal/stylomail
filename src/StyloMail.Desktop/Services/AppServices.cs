using StyloMail.Desktop.Api;
using StyloMail.Desktop.Models;

namespace StyloMail.Desktop.Services;

/// <summary>
/// The composition root. The one place that knows how the console is assembled.
/// </summary>
/// <remarks>
/// Shaped after mylo's <c>ReaderServices</c>, and it is a much smaller class for
/// a structural reason worth stating: that one owns a database, a refresh
/// scheduler, an image cache and four coordinators, because mylo is the system.
/// This console owns an <see cref="HttpClient"/> and a keychain, because spec
/// 10.1 makes it an API client and nothing else. If this class starts growing
/// components, the decision that keeps the privilege model in one place has
/// been undone.
/// </remarks>
public sealed class AppServices : IDisposable
{
    /// <summary>
    /// How long a readiness probe may take before the console stops waiting.
    /// </summary>
    /// <remarks>
    /// Deliberately far shorter than the transport timeout. This call drives
    /// the sidebar's status line, so a Host that is not there has to be
    /// reported as not there promptly; leaving the operator watching a spinner
    /// for the full request timeout is the difference between a console that
    /// feels broken and one that says what is wrong.
    /// </remarks>
    private static readonly TimeSpan ReadinessDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounds a pooled connection's reuse, so a console left open for days
    /// eventually redials rather than talking to an address that has moved.
    /// </summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private bool _disposed;

    private AppServices(Uri hostAddress, HttpClient http, KeychainApiKeyProvider apiKey)
    {
        HostAddress = hostAddress;
        _http = http;
        ApiKey = apiKey;
    }

    /// <summary>Where this console points.</summary>
    public Uri HostAddress { get; }

    /// <summary>The keychain-backed key, exposed so the entry dialog can store and clear one.</summary>
    public KeychainApiKeyProvider ApiKey { get; }

    /// <summary>The console's entire view of the system.</summary>
    public StyloMailApiClient Client { get; private set; } = null!;

    /// <summary>
    /// Builds the console's services.
    /// </summary>
    /// <param name="hostAddress">Where the Host is.</param>
    /// <param name="keychain">Where the API key lives.</param>
    /// <param name="transport">
    /// The transport to send over, or null for a real socket. Supplied only by
    /// tests and the screenshot harness, which need the console's own decisions
    /// exercised without a Host running.
    /// </param>
    public static AppServices Create(Uri hostAddress, IKeychain keychain, HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(hostAddress);
        ArgumentNullException.ThrowIfNull(keychain);

        var http = new HttpClient(transport ?? new SocketsHttpHandler
        {
            // The console holds one connection pool to one Host. Retiring
            // pooled connections is the same long-uptime fix mylo applies: an
            // operator console is meant to stay open, and a connection that is
            // never retired never picks up a DNS or address change.
            PooledConnectionLifetime = PooledConnectionLifetime,

            // Finite on purpose. The default is Infinite, which lets one
            // unreachable address consume an operator's entire patience.
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // Redirects are not followed. A Host that answers 302 is not the
            // Host this console was configured to talk to, and silently
            // following it would send an API key somewhere it was never
            // intended to go.
            AllowAutoRedirect = false,
        })
        {
            BaseAddress = hostAddress,

            // The per-call deadlines below are the real bounds; this is the
            // backstop for a call that forgets to set one.
            Timeout = TimeSpan.FromSeconds(30),
        };

        var provider = new KeychainApiKeyProvider(keychain);
        var services = new AppServices(hostAddress, http, provider);

        services.Client = new StyloMailApiClient(http, provider);

        return services;
    }

    /// <summary>
    /// Asks the Host how it is, and turns every possible answer into a
    /// <see cref="HostStatus"/>.
    /// </summary>
    /// <remarks>
    /// <b>This never throws for an expected failure.</b> The sidebar has to
    /// render something in every one of these cases, and a method that threw
    /// would push the mapping into an event handler where it would be written
    /// once per call site and diverge. Only cancellation propagates, because a
    /// cancelled check produced no answer about the Host at all.
    /// </remarks>
    public async Task<HostStatus> CheckHostAsync(CancellationToken cancellationToken = default)
    {
        // The key is checked before anything is asked, and this ordering is the
        // fix for a real first-run experience rather than a precaution.
        //
        // GET /health/ready is unauthenticated by design, so it answers happily
        // whatever key the console holds, including none. A console with no key
        // therefore probed its configured address, got whatever was there, and
        // reported that instead: on a Mac the default address is also AirPlay
        // Receiver's port, so the first thing a new operator saw was "the Host
        // refused the request". The one step they actually had to take, enter a
        // key, was not mentioned anywhere.
        if (!ApiKey.HasKey())
        {
            return HostStatus.FromFailure(new StyloMailApiException(
                StyloMailApiFailure.ApiKeyNotConfigured,
                "No API key is stored."));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReadinessDeadline);

        try
        {
            return HostStatus.From(await Client.GetReadinessAsync(deadline.Token).ConfigureAwait(false));
        }
        catch (StyloMailApiException failure)
        {
            return HostStatus.FromFailure(failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller's cancellation. The Host did not
            // answer in time, which from the operator's side is indistinguishable
            // from not answering at all, so it is reported as unreachable rather
            // than as a cancellation they did not ask for.
            return HostStatus.FromFailure(new StyloMailApiException(
                StyloMailApiFailure.Unreachable,
                "The Host did not answer in time."));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
