namespace StyloMail.Adaptive.Learning;

/// <summary>One recorded containment event.</summary>
public sealed record SendingIncident
{
    public required string TenantId { get; init; }

    public required string PrincipalId { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>
/// Recipient budget per authenticated principal, over a rolling window.
/// </summary>
/// <remarks>
/// <b>What this bounds is escape volume</b> — how much a compromised principal can send between
/// the compromise starting and the system noticing. That is a property of a window, which is why
/// the budget reopens on a clock rather than counting up for the lifetime of the process. A
/// lifetime cap is uncorrelated with escape volume: a sender's 501st recipient is not more
/// dangerous than their 5th, and treating it as though it were permanently blocks an account for
/// sending legitimate mail. Worse, the block is self-sustaining — every message it refuses keeps
/// the quota exhausted — so one newsletter becomes an irreversible stop with no signal.
///
/// <para>
/// Deliberately <b>not</b> part of profile state. Quotas bound how much damage a late detection
/// can do, so anything that can reset them also raises that bound — and profile eviction,
/// dehydration and baseline rollback are all routine operations that must not do that. Keeping
/// the ledger in its own store makes "eviction does not grant a fresh quota" a property of the
/// structure rather than a rule somebody has to remember.
/// </para>
///
/// <para>
/// Counts recipients, not messages: one message with five hundred recipients is five hundred
/// chances to reach somebody.
/// </para>
///
/// <para>
/// <b>In-memory, so a process restart grants every principal a fresh window.</b> That is
/// tolerable here and would not be if the budget were a lifetime cap: a restart can only ever
/// advance a window that was going to reopen anyway, whereas resetting a lifetime total would
/// undo the one thing it was meant to enforce. It stops being tolerable the moment this needs to
/// bound anything across restarts — at which point it has to become durable, and the window is
/// the smaller half of that change.
/// </para>
/// </remarks>
public sealed class SendingQuotaLedger
{
    /// <summary>
    /// Starting window, <b>unvalidated</b>.
    /// </summary>
    /// <remarks>
    /// Like every other threshold in this engine, this is a reasonable starting point rather than
    /// a tuned value. The spec is explicit that these come from representative replay data, and
    /// that data does not exist yet. Do not read it as measured.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    private readonly int _recipientsPerWindow;
    private readonly TimeSpan _window;
    private readonly Dictionary<(string TenantId, string PrincipalId), List<Reservation>> _reservations = [];

    /// <summary>
    /// Serialises every read-modify-write on the budget.
    /// </summary>
    /// <remarks>
    /// One lock rather than one per principal: the critical section is a few list operations,
    /// and a per-principal lock table would add its own mutable state to the very component whose
    /// safety depends on having none. The correctness requirement is only that check-then-act
    /// happens atomically.
    /// </remarks>
    private readonly Lock _gate = new();

    public SendingQuotaLedger(int recipientsPerWindow)
        : this(recipientsPerWindow, DefaultWindow)
    {
    }

    public SendingQuotaLedger(int recipientsPerWindow, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipientsPerWindow);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _recipientsPerWindow = recipientsPerWindow;
        _window = window;
    }

    /// <summary>Recipients allowed per principal per window.</summary>
    public int Capacity => _recipientsPerWindow;

    public TimeSpan Window => _window;

    /// <summary>
    /// Remaining budget in the window ending at <paramref name="at"/>.
    /// </summary>
    public int Remaining(string tenantId, string principalId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        lock (_gate)
        {
            var entries = Entries((tenantId, principalId));
            Prune(entries, at);

            return RemainingCore(entries);
        }
    }

    /// <summary>
    /// Reserves budget before dispatch. Fails rather than going negative.
    /// </summary>
    /// <remarks>
    /// The check and the debit are one atomic step. Split apart, two threads can both observe
    /// enough budget and both spend it — which over-grants, and the budget is the only thing
    /// bounding how much a late detection lets escape.
    ///
    /// <para>
    /// <b>The instant is a parameter, not a clock the ledger holds.</b> A constructor-supplied
    /// <see cref="TimeProvider"/> would make correct replay a stated requirement rather than an
    /// enforced one: contexts are per-request and options are not, so a harness that fixes one
    /// clock and not the other moves the window with the wall clock, silently, exactly once. With
    /// the instant supplied per call there is no clock in here to be wrong about.
    /// </para>
    /// </remarks>
    public bool TryReserve(string tenantId, string principalId, int recipients, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipients);

        lock (_gate)
        {
            var entries = Entries((tenantId, principalId));
            Prune(entries, at);

            if (RemainingCore(entries) < recipients)
            {
                return false;
            }

            entries.Add(new Reservation(at, recipients));
            return true;
        }
    }

    /// <summary>
    /// Returns budget for recipients that were never dispatched — a rejection before
    /// acceptance, not a rollback of something that was sent.
    /// </summary>
    /// <returns>
    /// The number of recipients actually returned. <b>Read this value.</b>
    /// </returns>
    /// <remarks>
    /// <b>A return smaller than <paramref name="recipients"/> is a discrepancy to account for, not
    /// a routine clamp.</b> It means the caller gave back more than it ever reserved, so the
    /// caller's own tally and this ledger's have diverged and whoever holds the outer figure needs
    /// to reconcile. A caller that ignores the return is exactly where it started: the divergence
    /// is silent again, which is the failure this return value exists to make visible.
    ///
    /// <para>
    /// One benign cause of a shortfall: a reservation that aged out of the window before it was
    /// released. The budget reopened on its own, so there is nothing to give back. That needs the
    /// window to be shorter than one assessment, which is not true of the default — but it is why
    /// the caller should check the window before reading a shortfall as divergence.
    /// </para>
    ///
    /// <para>
    /// Releasing more than was reserved is deliberately <em>not</em> an error: releasing the same
    /// reservation twice on a retry path is a legitimate thing for a caller to do, and turning that
    /// into an exception would put a crash on a hot path. The budget also clamps at zero rather
    /// than going negative, because that is the safe direction — it can never manufacture headroom
    /// that policy has not granted.
    /// </para>
    /// </remarks>
    public int Release(string tenantId, string principalId, int recipients, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipients);

        lock (_gate)
        {
            var entries = Entries((tenantId, principalId));
            Prune(entries, at);

            var outstanding = recipients;
            var returned = 0;

            // Most recent first. A release undoes a reservation that was just made and did not
            // lead to dispatch, so it takes back what was most recently claimed — which also
            // returns the budget for the longest remaining part of the window. Taking the oldest
            // entries instead would give back capacity that was about to expire anyway, which is
            // relief in the arithmetic and none in practice.
            for (var i = entries.Count - 1; i >= 0 && outstanding > 0; i--)
            {
                var taken = Math.Min(entries[i].Recipients, outstanding);
                returned += taken;
                outstanding -= taken;

                if (taken == entries[i].Recipients)
                {
                    entries.RemoveAt(i);
                }
                else
                {
                    entries[i] = entries[i] with { Recipients = entries[i].Recipients - taken };
                }
            }

            return returned;
        }
    }

    private List<Reservation> Entries((string TenantId, string PrincipalId) key)
    {
        if (!_reservations.TryGetValue(key, out var entries))
        {
            entries = [];
            _reservations[key] = entries;
        }

        return entries;
    }

    /// <summary>Drops reservations whose window has closed.</summary>
    /// <remarks>
    /// Filters the whole list rather than stopping at the first live entry. Callers supply their
    /// own instants, and concurrent assessments can present them a few milliseconds out of order —
    /// so "entries are sorted ascending" is not an invariant this type can enforce, and a prune
    /// that relied on it would leave an expired reservation counted behind a live one. Scanning
    /// the list costs nothing next to the assumption it removes.
    /// </remarks>
    private void Prune(List<Reservation> entries, DateTimeOffset at) =>
        entries.RemoveAll(entry => entry.At + _window <= at);

    private int RemainingCore(List<Reservation> entries) =>
        Math.Max(0, _recipientsPerWindow - entries.Sum(entry => entry.Recipients));

    /// <summary>One claim on the budget, and when it stops counting.</summary>
    private sealed record Reservation(DateTimeOffset At, int Recipients);
}

/// <summary>
/// Append-only record of containment events.
/// </summary>
/// <remarks>
/// Kept apart from profile state for the same reason as quotas: a baseline rollback restores
/// what we believe about a principal, and must not also un-happen the fact that it was
/// quarantined last week.
/// </remarks>
public sealed class IncidentLog
{
    private readonly List<SendingIncident> _incidents = [];
    private readonly Lock _gate = new();

    public void Record(string tenantId, string principalId, string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _incidents.Add(new SendingIncident
            {
                TenantId = tenantId,
                PrincipalId = principalId,
                Reason = reason,
                RecordedAt = at,
            });
        }
    }

    public IReadOnlyList<SendingIncident> For(string tenantId, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        lock (_gate)
        {
            return [.. _incidents.Where(i => i.TenantId == tenantId && i.PrincipalId == principalId)];
        }
    }

    public IReadOnlyList<SendingIncident> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _incidents];
            }
        }
    }
}
