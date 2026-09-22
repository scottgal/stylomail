namespace StyloMail.AccessProxy.Sessions;

/// <summary>Held for the lifetime of a session; releasing returns the slot.</summary>
public sealed class SessionLease : IDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal SessionLease(Action release) => _release = release;

    /// <summary>Returns the slot. Idempotent, so a double dispose cannot free a slot twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release();
        }
    }
}

/// <summary>
/// Bounds how many sessions run at once, globally and per account.
/// </summary>
/// <remarks>
/// Two caps with two different jobs. The global cap protects this process — sockets, memory,
/// schedulers. The per-account cap protects one mailbox, and stops a single compromised client
/// credential from consuming the entire instance's capacity: without it, one attacker holding one
/// valid login could open every session slot and deny access to every other user.
///
/// <para>
/// The two are acquired separately and at different points, because they become answerable at
/// different times. The global slot is claimed before the greeting, since accepting a connection at
/// all costs resources. The account slot cannot be claimed until the client has authenticated and
/// we know which account it is — so it is a second, later acquisition rather than a single call,
/// and a session that fails the second one is refused after a successful login rather than before.
/// Collapsing them into one call would mean counting every unauthenticated connection against a
/// per-account bucket that no account owns yet.
/// </para>
///
/// <para>
/// The caps behave differently on saturation, and the difference is deliberate. Hitting the
/// <em>global</em> cap waits — a burst queues rather than failing, which is what the transport does
/// for the same reason (<c>SmtpBounds.MaxConcurrentConnections</c>). Hitting the <em>per-account</em>
/// cap refuses immediately, because an account at its session limit is not a burst to absorb but a
/// signal to stop: queueing there would only build a backlog of sessions all aimed at one mailbox,
/// and the client would sit waiting for a session the backend could not have served anyway.
/// </para>
///
/// <para>
/// One lock guards the counters. The critical section contains no I/O and no await, so contention is
/// a few instructions; correctness here is worth far more than the throughput a lock-free version
/// would buy.
/// </para>
/// </remarks>
public sealed class SessionLimiter : IDisposable
{
    private readonly AccessProxyBounds _bounds;
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, int> _perAccount = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _active;

    public SessionLimiter(AccessProxyBounds bounds)
    {
        _bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        _bounds.Validate();
        _global = new SemaphoreSlim(bounds.MaxConcurrentSessions, bounds.MaxConcurrentSessions);
    }

    /// <summary>Sessions currently held, across all accounts. For health reporting.</summary>
    public int ActiveSessions => Volatile.Read(ref _active);

    /// <summary>
    /// Claims a global session slot, waiting if the instance is at capacity.
    /// </summary>
    public async ValueTask<SessionLease> AcquireGlobalAsync(CancellationToken cancellationToken)
    {
        await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _active);
        return new SessionLease(() =>
        {
            Interlocked.Decrement(ref _active);
            _global.Release();
        });
    }

    /// <summary>
    /// Claims a slot for <paramref name="accountKey"/>, or returns null when that account is
    /// already at its session limit.
    /// </summary>
    /// <remarks>
    /// Null means "refuse this session" rather than "wait", per the type remarks. The caller must
    /// treat null as a refusal and must not retry it in a loop.
    /// </remarks>
    public SessionLease? TryAcquireAccount(string accountKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountKey);

        lock (_gate)
        {
            _perAccount.TryGetValue(accountKey, out var current);
            if (current >= _bounds.MaxSessionsPerAccount)
            {
                return null;
            }

            _perAccount[accountKey] = current + 1;
        }

        return new SessionLease(() =>
        {
            lock (_gate)
            {
                if (!_perAccount.TryGetValue(accountKey, out var current))
                {
                    return;
                }

                if (current <= 1)
                {
                    _perAccount.Remove(accountKey);
                }
                else
                {
                    _perAccount[accountKey] = current - 1;
                }
            }
        });
    }

    public void Dispose() => _global.Dispose();
}
