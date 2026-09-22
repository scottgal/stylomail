using StyloMail.AccessProxy.Accounts;
using StyloMail.AccessProxy.Backends;
using StyloMail.AccessProxy.Credentials;

namespace StyloMail.AccessProxy.Sessions;

/// <summary>The client's claimed credentials, held as bytes so they can be zeroed.</summary>
internal sealed class ClientCredentials : IDisposable
{
    public required string Login { get; init; }

    public required SecretValue Password { get; init; }

    public void Dispose() => Password.Dispose();
}

/// <summary>How a session ended. Carries no content and no credential.</summary>
public enum SessionOutcome
{
    /// <summary>The client authenticated and bytes were relayed until one side closed.</summary>
    Relayed = 0,

    /// <summary>The client's StyloMail credential was wrong, unknown, or disabled.</summary>
    ClientRejected = 1,

    /// <summary>
    /// The client authenticated but the backend credential could not be used, or the provider
    /// refused it.
    /// </summary>
    /// <remarks>
    /// Deliberately indistinguishable from <see cref="ClientRejected"/> as far as the client is
    /// concerned, both produce the same negative reply. The distinction exists only for operators,
    /// because the actions differ sharply: a client rejection is a user typing the wrong password,
    /// while this is a revoked or unusable backend credential that needs an operator or the user to
    /// re-enrol. Telling the client which one it was would confirm to an attacker that a login
    /// exists and that its StyloMail-side password was correct.
    /// </remarks>
    BackendUnavailable = 2,

    /// <summary>The account is already at its concurrent session limit.</summary>
    AccountLimitReached = 3,

    /// <summary>The client violated the protocol or exceeded a bound.</summary>
    ProtocolError = 4,

    /// <summary>The client stopped sending within a bound.</summary>
    Timeout = 5,

    /// <summary>The client went away before authenticating.</summary>
    ClientDisconnected = 6,
}

/// <summary>
/// The lifecycle every access protocol shares.
/// </summary>
/// <remarks>
/// Greet, take the client's credentials, verify them, obtain a backend channel, relay. The protocol
/// subclasses supply only the parts that actually differ, the greeting text, how a credential is
/// spelled in that protocol, and how accept/reject is worded, so the security-relevant steps
/// happen exactly once, here, rather than once per protocol with three chances to get one wrong.
///
/// <para>
/// <b>The order of the last two steps is the load-bearing decision.</b> The backend is connected and
/// authenticated <em>before</em> the client is told its login succeeded. The tempting alternative, /// say yes immediately, then connect, reads as friendlier and is wrong twice over: a client told
/// "OK" proceeds to issue real commands that then go nowhere, and a revoked backend credential
/// would surface as a mysteriously dead session rather than as an authentication failure. Spec §9.5
/// risk 4 requires a revoked credential to <em>fail closed and surface as an authentication
/// failure</em>, and the only way to honour that is to have the answer before you give it.
/// </para>
///
/// <para>
/// Instances are single-use and hold the channel they were started with. A session is one
/// connection; reusing an instance for a second would carry the first client's state across.
/// </para>
/// </remarks>
public abstract class AccessProxySessionBase
{
    private readonly IStyloMailAccountStore _accounts;
    private readonly IStyloMailPasswordHasher _passwordHasher;
    private readonly BackendCredentialResolver _credentials;
    private readonly IBackendConnector _backend;
    private readonly SessionLimiter _limiter;
    private readonly IRetrievalObserver? _observer;
    private IDuplexChannel? _client;
    private ProtocolLineWriter? _writer;

    protected AccessProxySessionBase(
        IStyloMailAccountStore accounts,
        IStyloMailPasswordHasher passwordHasher,
        BackendCredentialResolver credentials,
        IBackendConnector backend,
        SessionLimiter limiter,
        AccessProxyBounds bounds,
        TimeProvider timeProvider,
        IRetrievalObserver? observer = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Bounds.Validate();
        _observer = observer;
    }

    /// <summary>The protocol this session terminates.</summary>
    public abstract string ProtocolName { get; }

    protected abstract BackendProtocol BackendProtocol { get; }

    protected AccessProxyBounds Bounds { get; }

    protected TimeProvider TimeProvider { get; }

    /// <summary>The client channel. Only valid during <see cref="RunAsync"/>.</summary>
    protected IDuplexChannel Client =>
        _client ?? throw new InvalidOperationException("The session is not running.");

    private protected ProtocolLineWriter Writer =>
        _writer ?? throw new InvalidOperationException("The session is not running.");

    /// <summary>Sends the protocol's opening banner.</summary>
    protected abstract ValueTask WriteGreetingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the client's claimed credentials, or null if it disconnected before offering any.
    /// </summary>
    /// <remarks>
    /// Implementations must extract only what the protocol carries and must not validate it,     /// verification happens once, in <see cref="RunAsync"/>, so every protocol is held to the same
    /// check.
    /// </remarks>
    private protected abstract ValueTask<ClientCredentials?> ReadClientCredentialsAsync(
        BoundedLineReader reader,
        CancellationToken cancellationToken);

    /// <summary>Tells the client its credentials were accepted and the session may proceed.</summary>
    protected abstract ValueTask WriteAuthenticatedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tells the client authentication failed.
    /// </summary>
    /// <remarks>
    /// Used for a wrong client password, an unknown account, a disabled account, an account at its
    /// session limit, and an unusable backend credential alike. The client is told one thing because
    /// distinguishing them would tell an attacker which logins exist and which passwords are right.
    /// </remarks>
    protected abstract ValueTask WriteAuthenticationFailedAsync(CancellationToken cancellationToken);

    /// <summary>Runs one client session to completion.</summary>
    public async Task<SessionOutcome> RunAsync(IDuplexChannel client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_client is not null)
        {
            throw new InvalidOperationException("A session instance runs exactly one connection.");
        }

        _client = client;
        _writer = new ProtocolLineWriter(client.Output);

        using var globalSlot = await _limiter.AcquireGlobalAsync(cancellationToken).ConfigureAwait(false);

        var reader = new BoundedLineReader(client.Input, Bounds, TimeProvider);

        try
        {
            await WriteGreetingAsync(cancellationToken).ConfigureAwait(false);

            using var authentication = new TimeoutScope(
                Bounds.AuthenticationTimeout, TimeProvider, cancellationToken);

            ClientCredentials? claimed;
            try
            {
                claimed = await ReadClientCredentialsAsync(reader, authentication.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SessionOutcome.Timeout;
            }

            if (claimed is null)
            {
                return SessionOutcome.ClientDisconnected;
            }

            using (claimed)
            {
                var account = await _accounts
                    .FindByLoginAsync(claimed.Login, cancellationToken)
                    .ConfigureAwait(false);

                if (!VerifyClientCredential(account, claimed.Password))
                {
                    await WriteAuthenticationFailedAsync(cancellationToken).ConfigureAwait(false);
                    return SessionOutcome.ClientRejected;
                }

                // Non-null because VerifyClientCredential only returns true for a usable account.
                var resolved = account!;

                var accountSlot = _limiter.TryAcquireAccount(resolved.AccountId);
                if (accountSlot is null)
                {
                    await WriteAuthenticationFailedAsync(cancellationToken).ConfigureAwait(false);
                    return SessionOutcome.AccountLimitReached;
                }

                using (accountSlot)
                {
                    return await RelayAuthenticatedSessionAsync(resolved, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (AccessProxyProtocolException)
        {
            return SessionOutcome.ProtocolError;
        }
        catch (AccessProxyTimeoutException)
        {
            return SessionOutcome.Timeout;
        }
        catch (IOException)
        {
            // The client vanished. Routine on an internet-facing listener.
            return SessionOutcome.ClientDisconnected;
        }
    }

    private async Task<SessionOutcome> RelayAuthenticatedSessionAsync(
        StyloMailAccount account,
        CancellationToken cancellationToken)
    {
        IDuplexChannel backend;
        try
        {
            backend = await _backend.ConnectAsync(
                new BackendConnectionRequest
                {
                    AccountId = account.BackendAccountId,
                    Protocol = BackendProtocol,
                    TimeProvider = TimeProvider,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialUnavailableException)
        {
            // Spec §9.5 risk 4: revoked, missing or unusable. Fail closed, tell the client its
            // authentication failed, and do not retry, a rejected credential presented again is a
            // failed login attempt against the user's own account.
            await WriteAuthenticationFailedAsync(cancellationToken).ConfigureAwait(false);
            return SessionOutcome.BackendUnavailable;
        }
        catch (BackendAuthenticationRejectedException)
        {
            await WriteAuthenticationFailedAsync(cancellationToken).ConfigureAwait(false);
            return SessionOutcome.BackendUnavailable;
        }

        await using (backend.ConfigureAwait(false))
        {
            // The client is told only now, with a backend already authenticated behind it.
            await WriteAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

            var bounded = await ByteRelay
                .RunAsync(Client, backend, Bounds, TimeProvider, _observer, cancellationToken)
                .ConfigureAwait(false);

            return bounded ? SessionOutcome.Timeout : SessionOutcome.Relayed;
        }
    }

    /// <summary>
    /// Verifies the client's credential, paying the same cost whether or not the login exists.
    /// </summary>
    /// <remarks>
    /// The unknown-login branch still runs a full derivation against
    /// <see cref="IStyloMailPasswordHasher.DecoyVerifier"/>. Skipping it would make an unknown login
    /// answer in microseconds and a known one take the full PBKDF2 cost, which is a reliable oracle
    /// for enumerating which addresses have accounts. See the hasher's remarks.
    /// </remarks>
    private bool VerifyClientCredential(StyloMailAccount? account, SecretValue password)
    {
        if (account is null || account.Disabled)
        {
            _ = _passwordHasher.Verify(password.Utf8, _passwordHasher.DecoyVerifier);
            return false;
        }

        return _passwordHasher.Verify(password.Utf8, account.PasswordHash);
    }
}
