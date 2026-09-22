namespace StyloMail.Adaptive.Profiles;

/// <summary>One recipient this principal has addressed, and when we last saw them.</summary>
public sealed record RecipientEntry
{
    public required string Key { get; init; }

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; init; }
}

/// <summary>
/// Which recipients a sender has addressed, bounded in cardinality and time.
/// </summary>
/// <remarks>
/// An account fanning out to strangers is the shape this system exists to catch, and "distinct
/// recipients" is the measurement that names it. Counting addresses instead would call fifty
/// messages to one colleague fan-out, so the two are tracked separately.
///
/// <para>
/// <b>Two structures, because the two questions have different failure modes.</b> Distinct counts
/// come from a capped, windowed set — cheap and exact while it has room, and a <b>floor</b> once
/// <see cref="Truncated"/> says it stopped being complete. Novelty comes from a
/// <see cref="RecipientBloomFilter"/>, which never truncates and has no false negatives, so it
/// keeps answering the one question a capped set has to give up on.
/// </para>
///
/// <para>
/// <b>The direction of the error decides the encoding.</b> A truncated count can only under-state,
/// and under-stating cannot manufacture alarm, so it is reported as a floor. Novelty over-states,
/// so where it cannot be established it is <see langword="null"/> — unknown — rather than zero.
/// </para>
///
/// <para>
/// Keys are pseudonymised by the caller, like every other identifier in this engine. Not thread-safe
/// on its own; it lives inside a profile, which is not shared.
/// </para>
/// </remarks>
public sealed class RecipientHistory
{
    /// <summary>
    /// Keys held by the distinct-count set, <b>unvalidated</b>.
    /// </summary>
    /// <remarks>
    /// A starting point rather than a tuned value, like every other threshold in this engine. It
    /// covers the overwhelming majority of senders exactly, and the ones it does not are the ones
    /// worth watching — which is why truncation is reported rather than hidden.
    /// </remarks>
    public const int DefaultCapacity = 256;

    /// <summary>How long a recipient stays counted. <b>Unvalidated</b>, as above.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>Expected distinct recipients the membership filter is sized for. <b>Unvalidated</b>.</summary>
    public const int DefaultFilterCapacity = 4096;

    /// <summary>Target false-positive rate for the membership filter. <b>Unvalidated</b>.</summary>
    public const double DefaultFilterFalsePositiveRate = 0.01;

    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, RecipientEntry> _recipients = new(StringComparer.Ordinal);

    public RecipientHistory(
        int capacity = DefaultCapacity,
        TimeSpan? window = null,
        RecipientBloomFilter? seen = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var span = window ?? DefaultWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);

        _capacity = capacity;
        _window = span;
        Seen = seen ?? new RecipientBloomFilter(DefaultFilterCapacity, DefaultFilterFalsePositiveRate);
    }

    /// <summary>
    /// True once the distinct-count set stopped being complete.
    /// </summary>
    /// <remarks>
    /// Set when an entry is evicted to make room or ages out of the window, which makes
    /// <see cref="DistinctSince"/> a floor. It does <b>not</b> affect <see cref="NovelCount"/>:
    /// novelty is answered by a structure that never forgets.
    /// </remarks>
    public bool Truncated { get; private set; }

    /// <summary>
    /// False when this history does not cover the principal's past.
    /// </summary>
    /// <remarks>
    /// A filter is only evidence of absence if it was fed everything the principal has ever done.
    /// A history restored from storage has been; a history silently starting empty part-way through
    /// a principal's life has not, and reading "not present" from it would report every recipient
    /// as novel — manufacturing the exact alarm this structure exists to avoid, at scale.
    /// </remarks>
    public bool IsComplete { get; private set; } = true;

    /// <summary>Keys currently held for distinct counting. A floor once <see cref="Truncated"/>.</summary>
    public int Count => _recipients.Count;

    public TimeSpan Window => _window;

    public int Capacity => _capacity;

    /// <summary>How many times a key was presented to this history, for diagnostics.</summary>
    public int ObservedKeyCount => Seen.Count;

    /// <summary>The membership filter behind <see cref="NovelCount"/>, exposed for persistence.</summary>
    public RecipientBloomFilter Seen { get; }

    /// <summary>Entries held for distinct counting, exposed for persistence.</summary>
    public IReadOnlyList<RecipientEntry> Entries => [.. _recipients.Values];

    public void Record(IEnumerable<string> recipientKeys, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(recipientKeys);

        Prune(at);

        foreach (var key in recipientKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            // Every key goes to the filter, including ones the capped set has no room for. That is
            // what keeps novelty answerable after the set saturates.
            Seen.Add(key);

            if (_recipients.TryGetValue(key, out var existing))
            {
                _recipients[key] = existing with { LastSeen = at };
                continue;
            }

            if (_recipients.Count >= _capacity)
            {
                // Turning a recipient away is the moment the distinct count stops being a
                // measurement. Flagged here rather than at read time because it is a property of
                // the history, not of the question being asked.
                Truncated = true;
                continue;
            }

            _recipients[key] = new RecipientEntry { Key = key, FirstSeen = at, LastSeen = at };
        }
    }

    /// <summary>
    /// Distinct recipients addressed since <paramref name="from"/>, counting last-seen.
    /// </summary>
    /// <remarks>
    /// A floor when <see cref="Truncated"/>: the true count can only be larger, never smaller.
    /// </remarks>
    public int DistinctSince(DateTimeOffset from) =>
        _recipients.Values.Count(entry => entry.LastSeen >= from);

    /// <summary>
    /// How many of these recipients this principal has never addressed, or <see langword="null"/>
    /// when that cannot be established.
    /// </summary>
    /// <remarks>
    /// Null only when the history does not cover the principal's past — never merely because the
    /// distinct-count set is full. A full set is exactly the case the membership filter exists to
    /// keep answering through.
    /// </remarks>
    public int? NovelCount(IEnumerable<string> recipientKeys)
    {
        ArgumentNullException.ThrowIfNull(recipientKeys);

        if (!IsComplete)
        {
            return null;
        }

        return recipientKeys.Count(key => !Seen.MightContain(key));
    }

    /// <summary>
    /// Records that this history does not cover the principal's past, so novelty is unanswerable.
    /// </summary>
    /// <remarks>
    /// Used when a profile is loaded and its history could not be brought with it. Without this the
    /// filter would start empty, report every recipient as novel, and manufacture alarm on the
    /// strongest signal the profile carries — the precise failure this type is built to prevent.
    /// </remarks>
    public void MarkIncomplete() => IsComplete = false;

    /// <summary>Restores a history previously serialised with <see cref="Entries"/> and <see cref="Seen"/>.</summary>
    public static RecipientHistory Restore(
        int capacity,
        TimeSpan window,
        IEnumerable<RecipientEntry> entries,
        RecipientBloomFilter seen,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(seen);

        var history = new RecipientHistory(capacity, window, seen)
        {
            Truncated = truncated,
        };

        foreach (var entry in entries)
        {
            history._recipients[entry.Key] = entry;
        }

        return history;
    }

    private void Prune(DateTimeOffset at)
    {
        var cutoff = at - _window;
        List<string>? expired = null;

        foreach (var (key, entry) in _recipients)
        {
            if (entry.LastSeen < cutoff)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _recipients.Remove(key);
        }

        // Aging out is the same statement as eviction for room: the distinct count is no longer a
        // measurement of everything addressed in the window.
        Truncated = true;
    }
}
