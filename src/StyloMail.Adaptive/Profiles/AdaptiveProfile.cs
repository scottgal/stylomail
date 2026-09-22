using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Temporal;

namespace StyloMail.Adaptive.Profiles;

/// <summary>An immutable snapshot of the trusted baseline, for correction and rollback.</summary>
public sealed record BaselineCheckpoint
{
    public required TrustedBaseline Baseline { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>
/// One profile's two stores of evidence, held apart from each other.
/// </summary>
/// <remarks>
/// The separation is the whole design. <see cref="Observed"/> answers "what has this principal
/// been doing", and accepts everything, including traffic that was refused.
/// <see cref="Baseline"/> answers "what does legitimate behaviour look like", and accepts only
/// samples that passed a provenance gate.
///
/// <para>
/// Anything that touches both is a bug: a sender that suddenly triples its volume must be
/// visible in observed state immediately, while the baseline that defines "normal" for that
/// sender should barely move — otherwise the first burst teaches the profile that bursts are
/// normal, which is precisely the poisoning this structure exists to prevent.
/// </para>
///
/// <para>
/// <b>One instance per profile, and never shared.</b> This type is mutable by design: it is
/// where a principal's history lives, so it cannot be made immutable and still do its job. The
/// guarantee is therefore the absence of sharing, not freedom from races — and it is the reason
/// <see cref="AdaptiveProfile"/> is deliberately <em>not</em> in the shareable set asserted by
/// <c>SharedStateTests</c>.
/// </para>
///
/// <para>
/// <b>Writing one back is the caller's problem, and the store offers two ways to do it.</b>
/// <c>Save</c> writes the whole profile you are holding, and refuses the write if another writer
/// moved the stored revision — so a lost update fails loudly rather than silently, and the caller
/// reloads and reapplies. <c>ApplyObservation</c> and <c>Update</c> avoid the conflict altogether
/// by doing the read-modify-write inside the store's write transaction, which is what a burst
/// needs: under a burst many callers hold the <em>same</em> profile, so a write-and-check design
/// lets one win a round and makes the rest retry.
/// </para>
///
/// <para>
/// What all of that guards against is silent by nature. For observed counters a dropped write
/// does not error — the abuse-bounding counts simply read low, and nothing downstream can tell
/// that from a quiet sender.
/// </para>
/// </remarks>
public sealed class AdaptiveProfile
{
    private readonly AdaptiveOptions _options;
    private readonly string _initialRegimeId;
    private readonly Dictionary<string, BucketSeries> _series;
    private Dictionary<string, EwmaState> _fastAverages = new(StringComparer.Ordinal);
    private Dictionary<string, EwmaState> _slowAverages = new(StringComparer.Ordinal);
    private RegimeCandidate? _candidate;

    public AdaptiveProfile(ProfileKey key, AdaptiveOptions? options = null, string initialRegimeId = "regime-0")
        : this(key, options, initialRegimeId, ObservedState.Empty, TrustedBaseline.Empty, series: null)
    {
        Baseline = Baseline with { RegimeId = initialRegimeId };
    }

    /// <summary>Reconstruction path used by the store when loading persisted state.</summary>
    internal AdaptiveProfile(
        ProfileKey key,
        AdaptiveOptions? options,
        string initialRegimeId,
        ObservedState observed,
        TrustedBaseline baseline,
        IReadOnlyDictionary<string, BucketSeries>? series,
        RecipientHistory? recipients = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialRegimeId);

        Key = key;
        _options = options ?? new AdaptiveOptions();
        _initialRegimeId = initialRegimeId;
        Observed = observed;
        Baseline = baseline;
        CurrentRegimeId = baseline.RegimeId ?? initialRegimeId;
        _series = CreateSeries(_options);
        Recipients = recipients
            ?? new RecipientHistory(_options.RecipientHistoryCapacity, _options.RecipientHistoryWindow);

        if (series is null)
        {
            return;
        }

        foreach (var (name, restored) in series)
        {
            _series[name] = restored;
        }
    }

    public ProfileKey Key { get; }

    /// <summary>
    /// Bucket series per trend window, keyed by window name.
    /// </summary>
    /// <remarks>
    /// Every observation lands in every window. The windows differ only in resolution —
    /// one bucket wide enough to see a burst, one wide enough that a burst disappears into it.
    /// </remarks>
    public IReadOnlyDictionary<string, BucketSeries> Series => _series;

    /// <summary>Replaces the fast/slow averages. Used only when restoring persisted state.</summary>
    internal void RestoreAverages(
        IReadOnlyDictionary<string, EwmaState> fast,
        IReadOnlyDictionary<string, EwmaState> slow)
    {
        _fastAverages = new Dictionary<string, EwmaState>(fast, StringComparer.Ordinal);
        _slowAverages = new Dictionary<string, EwmaState>(slow, StringComparer.Ordinal);
    }

    private static Dictionary<string, BucketSeries> CreateSeries(AdaptiveOptions options) =>
        new(StringComparer.Ordinal)
        {
            [options.Burst.Name] = new BucketSeries(options.Burst.BucketWidth),
            [options.Slow.Name] = new BucketSeries(options.Slow.BucketWidth),
        };

    /// <summary>All attempts, including rejected ones. Never a source of trust.</summary>
    public ObservedState Observed { get; private set; }

    /// <summary>Approved samples only. Never a record of everything seen.</summary>
    public TrustedBaseline Baseline { get; private set; }

    /// <summary>The regime the trusted baseline describes.</summary>
    public string CurrentRegimeId { get; private set; }

    /// <summary>The regime the baseline described before the most recent promotion.</summary>
    public string? PreviousRegimeId { get; private set; }

    /// <summary>
    /// When the current regime took over.
    /// </summary>
    /// <remarks>
    /// Bucket series span regime changes: observations either side of this instant were made
    /// under different baselines, and differencing across the boundary compares two different
    /// things. Recorded so the trend can withhold evidence until the window has moved past it.
    /// </remarks>
    public DateTimeOffset? RegimeChangedAt { get; private set; }

    /// <summary>A regime under evaluation, if one is accumulating. Never used for comparison.</summary>
    public RegimeCandidate? RegimeCandidate => _candidate;

    /// <summary>
    /// The stored revision this instance was loaded from, or <c>0</c> if it has never been saved.
    /// </summary>
    /// <remarks>
    /// The optimistic-concurrency token. <see cref="SqliteAdaptiveProfileStore"/> refuses a write
    /// whose token no longer matches, which is how a load-modify-save cycle that raced with another
    /// is caught instead of silently dropping one of the two updates.
    ///
    /// <para>
    /// Deliberately not <c>Baseline.Version</c>: that moves only on promotion, so an
    /// observation-only write would carry an unchanged token and overwrite anything that landed
    /// in between without noticing.
    /// </para>
    /// </remarks>
    public long PersistedRevision { get; internal set; }

    /// <summary>
    /// Which recipients this principal has addressed, bounded in cardinality and time.
    /// </summary>
    /// <remarks>
    /// Bounded by construction, and deliberately <em>not</em> cleared by <see cref="Dehydrate"/>:
    /// eviction exists to free expensive state, and discarding this would turn a bounded cost into
    /// a permanent loss of novelty reporting for a principal whose profile we still hold.
    /// </remarks>
    public RecipientHistory Recipients { get; }

    /// <summary>Fast trend average per dimension. Observed state, not trusted history.</summary>
    public IReadOnlyDictionary<string, EwmaState> FastAverages => _fastAverages;

    /// <summary>Slow trend average per dimension. Observed state, not trusted history.</summary>
    public IReadOnlyDictionary<string, EwmaState> SlowAverages => _slowAverages;

    /// <summary>
    /// Records an attempt. Unconditional by design.
    /// </summary>
    public void Observe(ProfileObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.RecipientCount);

        Observed = Observed.With(observation);

        if (observation.RecipientKeys is { Count: > 0 } recipientKeys)
        {
            Recipients.Record(recipientKeys, observation.ObservedAt);
        }

        foreach (var series in _series.Values)
        {
            series.Add(
                observation.ObservedAt,
                observation.Dimensions,
                observation.RecipientCount,
                observation.WasRejected);
        }

        if (observation.Dimensions is null)
        {
            return;
        }

        // Fast and slow averages live on the observed side: they describe what is happening,
        // and nothing here is allowed to become an assertion that it is legitimate.
        foreach (var sample in observation.Dimensions.Dimensions)
        {
            if (sample.Value is null)
            {
                continue;
            }

            _fastAverages[sample.DimensionId] = Average(
                _fastAverages, sample.DimensionId, sample.Value.Value, observation.ObservedAt, _options.FastEwma);
            _slowAverages[sample.DimensionId] = Average(
                _slowAverages, sample.DimensionId, sample.Value.Value, observation.ObservedAt, _options.SlowEwma);
        }

        static EwmaState Average(
            Dictionary<string, EwmaState> averages,
            string dimensionId,
            double value,
            DateTimeOffset at,
            EwmaOptions options) =>
            averages.TryGetValue(dimensionId, out var existing)
                ? existing.Observe(value, at, options)
                : EwmaState.Empty.Observe(value, at, options);
    }

    /// <summary>
    /// Offers a labelled sample as trusted history.
    /// </summary>
    /// <remarks>
    /// Rejections are returned rather than thrown: declining to learn from an unapproved sample
    /// is ordinary operation, not an error, and the caller needs the reason for the ledger.
    /// </remarks>
    public PromotionOutcome Promote(TrustedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (!BaselineLabelPolicy.IsTrustedProvenance(sample.Provenance))
        {
            return PromotionOutcome.RejectedUntrustedProvenance;
        }

        if (!BaselineLabelPolicy.AppliesToScope(sample.Provenance, Key.Scope))
        {
            return PromotionOutcome.RejectedProvenanceOutOfScope;
        }

        if (Baseline.IsFrozen)
        {
            // Freezing is a containment control, not a pause: a suspected-compromised
            // principal must not be able to legitimise its own traffic by continuing to send.
            return PromotionOutcome.RejectedBaselineFrozen;
        }

        if (_candidate is not null)
        {
            // While a new regime is under evaluation the incumbent baseline is untouched —
            // that is precisely what makes the evaluation an evaluation.
            _candidate.Add(sample);
            return PromotionOutcome.CandidateRecorded;
        }

        Baseline = Baseline
            .WithSample(sample, sample.RecordedAt, _options.MaxBaselineShiftPerUpdate)
            .WithRebuiltScale(_options.Scale);

        return PromotionOutcome.Promoted;
    }

    /// <summary>
    /// Starts evaluating a behaviour pattern as a possible new regime.
    /// </summary>
    public void BeginRegime(string regimeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regimeId);

        if (string.Equals(regimeId, CurrentRegimeId, StringComparison.Ordinal))
        {
            _candidate = null;
            return;
        }

        _candidate = new RegimeCandidate(
            regimeId,
            TrustedBaseline.Empty with { RegimeId = regimeId },
            _options);
    }

    /// <summary>
    /// Promotes the candidate to be the trusted baseline, if it has earned it.
    /// </summary>
    public bool PromoteRegime()
    {
        if (_candidate is null || !_candidate.IsPromotable)
        {
            return false;
        }

        // The newly promoted regime starts its own version line rather than continuing the
        // old one: the numbers describe a different behaviour, and pretending otherwise
        // would make a rollback to "version 12" ambiguous.
        Baseline = _candidate.Provisional with { Version = Baseline.Version + 1 };
        PreviousRegimeId = CurrentRegimeId;
        RegimeChangedAt = Baseline.LastPromotedAt;
        CurrentRegimeId = _candidate.RegimeId;
        _candidate = null;

        return true;
    }

    /// <summary>
    /// Stops trusted learning while compromise is suspected.
    /// </summary>
    /// <remarks>
    /// Observation continues untouched — containment needs the rate, and restoring the
    /// baseline later must not be confused with restoring the quota.
    /// </remarks>
    public void FreezeBaseline(string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Baseline = Baseline with
        {
            IsFrozen = true,
            FreezeReason = reason,
            FrozenAt = at,
        };
    }

    public void UnfreezeBaseline(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Baseline = Baseline with
        {
            IsFrozen = false,
            FreezeReason = null,
            FrozenAt = null,
        };
    }

    /// <summary>Snapshots the trusted baseline so a correction can be undone.</summary>
    public BaselineCheckpoint CaptureBaselineCheckpoint(DateTimeOffset at) =>
        new() { Baseline = Baseline, CapturedAt = at };

    /// <summary>
    /// Restores an earlier trusted baseline.
    /// </summary>
    /// <remarks>
    /// Three things this deliberately does not do. It does not restore spent quota: rolling
    /// back what we believe is not the same as un-sending mail, and a rollback that also
    /// refilled the budget would be an attacker's reset button. It does not clear the observed
    /// counters, for the same reason. And it does not lift a freeze, because the reason for
    /// containment is a fact about the present, not about the baseline that was in force when
    /// it was applied.
    /// </remarks>
    public void RollbackBaseline(BaselineCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        Baseline = checkpoint.Baseline with
        {
            IsFrozen = Baseline.IsFrozen,
            FreezeReason = Baseline.FreezeReason,
            FrozenAt = Baseline.FrozenAt,
        };
        CurrentRegimeId = Baseline.RegimeId ?? _initialRegimeId;
    }

    /// <summary>
    /// Frees the expensive profile state while keeping the cheap counters.
    /// </summary>
    /// <remarks>
    /// Dehydrating is not a reset. Deleting a profile outright would drop
    /// <see cref="Observed"/> with it, which is how an eviction becomes a way to earn a fresh
    /// quota — so eviction dehydrates, and the durable quota ledger is never part of profile
    /// state in the first place.
    /// </remarks>
    public void Dehydrate()
    {
        Baseline = TrustedBaseline.Empty with { RegimeId = CurrentRegimeId };
        _fastAverages = new Dictionary<string, EwmaState>(StringComparer.Ordinal);
        _slowAverages = new Dictionary<string, EwmaState>(StringComparer.Ordinal);
        _candidate = null;
    }
}
