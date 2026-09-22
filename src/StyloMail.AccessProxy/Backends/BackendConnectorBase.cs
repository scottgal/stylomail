using StyloMail.AccessProxy.Credentials;
using StyloMail.AccessProxy.Sessions;

namespace StyloMail.AccessProxy.Backends;

/// <summary>
/// Opens a raw, unauthenticated channel to a provider.
/// </summary>
/// <remarks>
/// The network seam. Separated from <see cref="IBackendConnector"/> so the authentication dialogue, /// which is the part with the credential in it, and the part worth testing exhaustively, can be
/// driven against an in-memory channel with no socket anywhere in sight. Everything above this
/// interface is testable without a network; everything below it is a socket and nothing else.
/// </remarks>
public interface IBackendTransport
{
    /// <summary>Opens an unauthenticated channel. Does not read the greeting or authenticate.</summary>
    ValueTask<IDuplexChannel> OpenAsync(BackendConnectionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Authenticates a backend channel using whatever credential the account holds.
/// </summary>
/// <remarks>
/// This is the code that consumes the credential seam, and it is worth reading as the proof that the
/// seam is real: <b>there is no mention of a password, a token, an app password or OAuth anywhere in
/// this class or its subclasses.</b> They receive an <see cref="IBackendAuthenticator"/> and a
/// <see cref="BackendAuthStyle"/>, and they frame opaque tokens accordingly. Swapping app passwords
/// for OAuth changes nothing here, the same <see cref="BackendAuthStyle.Sasl"/> path runs, with a
/// different mechanism string that the provider supplied.
///
/// <para>
/// The only branch anywhere in the proxy that could be mistaken for a credential-kind check is
/// <see cref="BackendAuthStyle"/>, and it is a statement about wire framing rather than about what
/// the credential is. Two credential kinds that share a style are indistinguishable from here,
/// which is precisely the property spec §9.5 asks for.
/// </para>
/// </remarks>
public abstract class BackendConnectorBase : IBackendConnector
{
    private readonly IBackendTransport _transport;
    private readonly BackendCredentialResolver _credentials;

    protected BackendConnectorBase(
        IBackendTransport transport,
        BackendCredentialResolver credentials,
        AccessProxyBounds bounds)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Bounds.Validate();
    }

    public abstract string ProviderLabel { get; }

    private protected AccessProxyBounds Bounds { get; }

    protected abstract BackendProtocol Protocol { get; }

    public async ValueTask<IDuplexChannel> ConnectAsync(
        BackendConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The credential is resolved before the socket is opened, so an account with a revoked or
        // missing credential never causes an outbound connection to the provider at all. That keeps
        // a revoked credential from producing even the connection attempt a provider might
        // rate-limit, flag, or count as a failed login.
        var authenticator = await _credentials
            .ResolveAsync(request.AccountId, Protocol, cancellationToken)
            .ConfigureAwait(false);

        using (authenticator)
        {
            var channel = await _transport.OpenAsync(request, cancellationToken).ConfigureAwait(false);

            try
            {
                using var timeout = new TimeoutScope(
                    Bounds.BackendAuthenticationTimeout, request.TimeProvider, cancellationToken);

                var reader = new BoundedLineReader(channel.Input, Bounds, request.TimeProvider);
                var writer = new ProtocolLineWriter(channel.Output);

                await AuthenticateAsync(channel, reader, writer, authenticator, timeout.Token)
                    .ConfigureAwait(false);

                return channel;
            }
            catch
            {
                // A channel that failed to authenticate is never handed back and never reused.
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Drives the provider-specific authentication dialogue.</summary>
    private protected abstract ValueTask AuthenticateAsync(
        IDuplexChannel channel,
        BoundedLineReader reader,
        ProtocolLineWriter writer,
        IBackendAuthenticator authenticator,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads and checks the provider's opening banner, which every protocol here sends before
    /// anything else.
    /// </summary>
    /// <param name="acceptedPrefixes">
    /// The banner prefixes that mean "proceed". Anything else is treated as a refusal, so an
    /// unrecognised banner fails closed rather than being read as consent.
    /// </param>
    private protected async ValueTask ReadGreetingAsync(
        BoundedLineReader reader,
        IReadOnlyList<string> acceptedPrefixes,
        CancellationToken cancellationToken)
    {
        var greeting = await reader
            .ReadLineAsync(Bounds.BackendAuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BackendAuthenticationRejectedException(
                $"{ProviderLabel} closed the connection without sending a greeting.");

        foreach (var prefix in acceptedPrefixes)
        {
            if (greeting.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new BackendAuthenticationRejectedException($"{ProviderLabel} refused the connection.");
    }

    /// <summary>Reads one reply line, failing closed if the backend simply goes away.</summary>
    private protected async ValueTask<string> ReadReplyAsync(
        BoundedLineReader reader,
        CancellationToken cancellationToken)
        => await reader
            .ReadLineAsync(Bounds.BackendAuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BackendAuthenticationRejectedException(
                $"The {ProviderLabel} backend closed the connection during authentication.");

    /// <summary>Caps the number of challenge/response rounds.</summary>
    /// <remarks>
    /// A provider that keeps issuing challenges would otherwise hold a session, and the credential
    /// exchange, open indefinitely. Exceeding it is a protocol failure, not a credential failure.
    /// </remarks>
    private protected void CheckRoundBudget(int round)
    {
        if (round > Bounds.MaxBackendAuthRounds)
        {
            throw new AccessProxyProtocolException(
                $"{ProviderLabel} exceeded {Bounds.MaxBackendAuthRounds} authentication rounds.");
        }
    }
}
