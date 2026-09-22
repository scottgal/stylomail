namespace StyloMail.Queue;

/// <summary>
/// Bounds and timings for the durable delivery queue.
/// </summary>
/// <remarks>
/// Every value here is a <em>bound</em> rather than a tuning knob. Bounded queue depth, bounded
/// attempts and a bounded retry lifetime are correctness properties of a store-and-forward
/// component: an unbounded queue is a denial-of-service vector against the spool, and an
/// unbounded retry is a message that is never delivered and never given up on.
///
/// <para>
/// <b>Time is injected.</b> Lease expiry, retry backoff and terminal expiry are all time-dependent,
/// and time-dependent durability logic that cannot be driven from a controllable clock is not
/// verifiable, a test that waits five real minutes for a lease to expire does not get written.
/// Nothing in this assembly reads the wall clock directly.
/// </para>
/// </remarks>
public sealed record QueueOptions
{
    /// <summary>Source of "now" for every lease, backoff and expiry decision.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long a worker may hold a claimed item before recovery may reclaim it.
    /// </summary>
    /// <remarks>
    /// This is the crash window. Too short and a slow-but-alive worker has its work reclaimed
    /// underneath it (producing a duplicate delivery); too long and a crashed worker strands a
    /// message. It must comfortably exceed the upstream delivery timeout.
    /// </remarks>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>First retry delay. Doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Ceiling on the retry delay, so backoff cannot grow without bound.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Default lifetime of an accepted message before it is abandoned.
    /// </summary>
    /// <remarks>
    /// The per-message <c>expires_at</c> is stamped at acceptance from this value (or from an
    /// explicit deadline supplied with the submission). At expiry the item becomes
    /// <c>TerminalFailure</c>, the message is given up on and surfaced, never silently dropped.
    /// </remarks>
    public TimeSpan RetryExpiry { get; init; } = TimeSpan.FromHours(48);

    /// <summary>Attempts allowed per recipient before that recipient is given up on.</summary>
    public int MaxAttemptsPerRecipient { get; init; } = 12;

    /// <summary>
    /// Hops a message may have already taken before we refuse to add another.
    /// </summary>
    /// <remarks>
    /// The loop guard. A message handed back to us after travelling through our own infrastructure
    /// is a mail loop, and the correct response is to decline it rather than to keep it circulating.
    /// Refusal happens <em>before</em> acceptance.
    /// </remarks>
    public int MaxHops { get; init; } = 20;

    /// <summary>Maximum size of a single accepted payload.</summary>
    public long MaxPayloadBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum non-terminal queue items per tenant.
    /// </summary>
    /// <remarks>
    /// Bounds <em>delivery capacity</em>: one tenant cannot occupy the whole queue and starve the
    /// others of the workers that drain it.
    /// </remarks>
    public int MaxQueuedItemsPerTenant { get; init; } = 10_000;

    /// <summary>
    /// Maximum spool bytes attributed to one tenant.
    /// </summary>
    /// <remarks>
    /// Bounds the <em>spool</em> rather than the queue: it counts every payload still on disk,
    /// including those of terminal items awaiting purge, because those are the bytes that can
    /// actually fill the volume. This is the counter that stops one tenant exhausting the disk.
    /// </remarks>
    public long MaxLivePayloadBytesPerTenant { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a terminal item's payload is retained before <c>Purge</c> may delete it.
    /// </summary>
    /// <remarks>
    /// A configurable engineering default, not legal guidance, the operator owns the real number.
    /// The metadata row survives purge; only the bytes go.
    /// </remarks>
    public TimeSpan TerminalPayloadRetention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Minimum age before an unreferenced payload may be swept as an orphan.
    /// </summary>
    /// <remarks>
    /// <b>This window is not optional.</b> Acceptance writes the payload and only then commits the
    /// metadata row that references it, so between those two steps the payload of an <em>in-flight</em>
    /// acceptance is indistinguishable from a true orphan. A sweeper acting on a shorter window
    /// would delete a payload microseconds before its metadata commits, manufacturing exactly the
    /// "metadata pointing at a payload that does not exist" state this subsystem exists to prevent.
    /// It must comfortably exceed the acceptance critical section.
    /// </remarks>
    public TimeSpan OrphanSweepMinimumAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Default hold window when policy holds a recipient without naming a deadline.</summary>
    public TimeSpan DefaultHoldWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Fractional jitter applied to retry backoff, spread deterministically across a range.
    /// </summary>
    /// <remarks>
    /// Jitter is derived from the queue id rather than a random source, so an item's schedule is a
    /// pure function of its identity, reproducible in tests and stable across recoveries, while
    /// still de-synchronising items that failed at the same moment.
    /// </remarks>
    public double BackoffJitterFraction { get; init; } = 0.2;

    /// <summary>
    /// How long a connection waits for another writer's lock before giving up with SQLITE_BUSY.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately equal to the driver's own default</b> (<c>SqliteCommand.CommandTimeout</c>,
    /// 30 seconds), because the point of stating it here is to make the policy visible and tunable,
    /// not to change it. Setting a smaller number is an unforced reduction: it shortens the window
    /// in which a worker waits out a concurrent recovery sweep instead of failing, and the failure
    /// it produces looks like a bug in the caller rather than a lock policy.
    /// </remarks>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the bounds. Called by the store's constructor.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(TimeProvider);

        Positive(LeaseDuration, nameof(LeaseDuration));
        Positive(BaseBackoff, nameof(BaseBackoff));
        Positive(MaxBackoff, nameof(MaxBackoff));
        Positive(RetryExpiry, nameof(RetryExpiry));
        Positive(TerminalPayloadRetention, nameof(TerminalPayloadRetention));
        Positive(OrphanSweepMinimumAge, nameof(OrphanSweepMinimumAge));
        Positive(DefaultHoldWindow, nameof(DefaultHoldWindow));
        Positive(BusyTimeout, nameof(BusyTimeout));

        if (MaxBackoff < BaseBackoff)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBackoff), MaxBackoff, "MaxBackoff must not be smaller than BaseBackoff.");
        }

        AtLeastOne(MaxAttemptsPerRecipient, nameof(MaxAttemptsPerRecipient));
        AtLeastOne(MaxHops, nameof(MaxHops));
        AtLeastOne(MaxQueuedItemsPerTenant, nameof(MaxQueuedItemsPerTenant));

        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes), MaxPayloadBytes, "Must be positive.");
        }

        if (MaxLivePayloadBytesPerTenant < MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxLivePayloadBytesPerTenant),
                MaxLivePayloadBytesPerTenant,
                "A tenant's byte budget must be at least as large as a single permitted payload, " +
                "otherwise no message could ever be accepted.");
        }

        if (BackoffJitterFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BackoffJitterFraction), BackoffJitterFraction, "Jitter is a fraction in [0, 1].");
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
