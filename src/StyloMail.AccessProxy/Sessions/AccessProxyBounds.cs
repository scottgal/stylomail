namespace StyloMail.AccessProxy.Sessions;

/// <summary>
/// Every hard limit the access proxy enforces while holding a client session open.
/// </summary>
/// <remarks>
/// <b>These are features, not tuning.</b> Constraint 6 of this component's brief is explicit: a
/// proxy that can be made to hold unbounded resources is a denial-of-service vector against the
/// mail it exists to protect. The difference from the store-and-forward path is the exponent. A
/// hostile SMTP peer costs a worker for the length of one transaction, seconds. A hostile client
/// holds a session open for as long as it likes, and a proxy that lets it do that with unbounded
/// memory per session falls over at a far lower request rate than any MTA would.
///
/// <para>
/// The specific ways this happens, each answered by one bound below:
/// <list type="bullet">
/// <item>A client that connects and never authenticates pins a session slot indefinitely.</item>
/// <item>A client that authenticates and then goes silent holds both a client and a backend
/// connection, the backend one against a real provider with its own per-user session limits, so a
/// handful of stuck clients can lock the user out of their own Gmail.</item>
/// <item>A client that sends an endless authentication command makes the line reader allocate
/// without limit.</item>
/// <item>One account opening many sessions at once is a brute-force or resource-exhaustion pattern
/// aimed at a single mailbox.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Bounded memory is structural, not a number.</b> There is no "maximum message size" here,
/// because the relay never buffers a message: it pumps fixed-size buffers in both directions, so a
/// client fetching a 100 MB mailbox uses the same memory as one fetching a 1 KB note. The bounds
/// below cap what <em>is</em> accumulated, command lines during authentication, and the numbers
/// that are deliberately absent are as much a part of the design as the ones present.
/// </para>
///
/// <para>
/// <b>Time is injected.</b> Nothing here reads the wall clock; every timeout is driven through
/// <see cref="TimeProvider"/> so tests advance a clock rather than wait for one.
/// </para>
/// </remarks>
public sealed record AccessProxyBounds
{
    /// <summary>
    /// How long a client may take to complete authentication.
    /// </summary>
    /// <remarks>
    /// Covers greeting to credential accepted, including any SASL continuation rounds. Bounded
    /// separately from <see cref="IdleTimeout"/> because an unauthenticated session has consumed no
    /// backend resources yet, and should not be allowed to sit in the pool waiting.
    /// </remarks>
    public TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a session may sit with no bytes moving in either direction.
    /// </summary>
    /// <remarks>
    /// This is the bound that protects the <em>provider</em>, not us. An idle proxied session still
    /// holds a Gmail connection, and Gmail caps simultaneous IMAP connections per account; ten
    /// clients left open overnight would exhaust that cap and the user would find their own mail
    /// client unable to connect. Generous enough for a client idling between polls, bounded enough
    /// that abandoned sessions are reclaimed.
    /// </remarks>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Absolute ceiling on one session, however busy it is.</summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long to wait for the backend to accept a TCP connection.</summary>
    public TimeSpan BackendConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for the backend to answer the authentication exchange.</summary>
    public TimeSpan BackendAuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum bytes in one client command line during authentication.
    /// </summary>
    /// <remarks>
    /// Small on purpose. This reader only ever consumes authentication commands, a login, a SASL
    /// exchange, never message data, because after authentication the session becomes a byte relay.
    /// A limit generous enough for a FETCH literal would be a limit that lets a client starve the
    /// proxy, and it would be answering a question the relay design already makes moot.
    /// </remarks>
    public int MaxCommandLineBytes { get; init; } = 8 * 1024;

    /// <summary>
    /// Maximum lines a client may send before authentication completes.
    /// </summary>
    /// <remarks>
    /// The line-length bound alone still permits an unbounded number of short lines; a SASL LOGIN
    /// exchange needs a handful, so a cap of a few dozen is already far more than any real client
    /// uses and closes the loop.
    /// </remarks>
    public int MaxAuthenticationCommands { get; init; } = 32;

    /// <summary>Maximum rounds of a SASL challenge/response exchange with the backend.</summary>
    public int MaxBackendAuthRounds { get; init; } = 8;

    /// <summary>
    /// Bytes moved per relay read in each direction.
    /// </summary>
    /// <remarks>
    /// This is the whole memory footprint of a proxied message, and it is constant regardless of
    /// message size. Big enough to keep a large FETCH efficient, small enough that ten thousand
    /// concurrent sessions is a memory calculation a human can do in their head.
    /// </remarks>
    public int RelayBufferBytes { get; init; } = 16 * 1024;

    /// <summary>Maximum concurrent client sessions this instance will hold.</summary>
    public int MaxConcurrentSessions { get; init; } = 512;

    /// <summary>
    /// Maximum concurrent sessions for a single account.
    /// </summary>
    /// <remarks>
    /// Smaller than the global cap by orders of magnitude, because the failure it prevents is
    /// different: a global cap protects this process, while a per-account cap protects one user's
    /// mailbox from being hammered, and stops one compromised client credential from consuming the
    /// whole instance's capacity.
    /// </remarks>
    public int MaxSessionsPerAccount { get; init; } = 8;

    /// <summary>Validates the bounds. Called by the session that consumes them.</summary>
    internal void Validate()
    {
        Positive(AuthenticationTimeout, nameof(AuthenticationTimeout));
        Positive(IdleTimeout, nameof(IdleTimeout));
        Positive(MaxSessionDuration, nameof(MaxSessionDuration));
        Positive(BackendConnectTimeout, nameof(BackendConnectTimeout));
        Positive(BackendAuthenticationTimeout, nameof(BackendAuthenticationTimeout));

        AtLeastOne(MaxCommandLineBytes, nameof(MaxCommandLineBytes));
        AtLeastOne(MaxAuthenticationCommands, nameof(MaxAuthenticationCommands));
        AtLeastOne(MaxBackendAuthRounds, nameof(MaxBackendAuthRounds));
        AtLeastOne(RelayBufferBytes, nameof(RelayBufferBytes));
        AtLeastOne(MaxConcurrentSessions, nameof(MaxConcurrentSessions));
        AtLeastOne(MaxSessionsPerAccount, nameof(MaxSessionsPerAccount));

        if (MaxSessionsPerAccount > MaxConcurrentSessions)
        {
            // Otherwise the per-account cap can never be the thing that fires, and an operator
            // setting it would reasonably believe it was protecting something it is not.
            throw new ArgumentOutOfRangeException(
                nameof(MaxSessionsPerAccount),
                MaxSessionsPerAccount,
                "Cannot exceed MaxConcurrentSessions, or the per-account cap can never take effect.");
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
        }
    }

    private static void AtLeastOne(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be at least 1.");
        }
    }
}
