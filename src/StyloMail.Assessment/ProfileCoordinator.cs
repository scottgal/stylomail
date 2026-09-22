using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Storage;

namespace StyloMail.Assessment;

/// <summary>Profile write counters, for the operator surface and for tests.</summary>
/// <remarks>
/// <b>Two counters, because there are two paths and they should not be confused.</b> If
/// <see cref="Mutated"/> ever climbs at message rates, whole-profile writes have drifted onto the
/// ingest path, which is the regression that would otherwise be invisible, because it works.
/// </remarks>
public sealed class ProfileWriteStatistics
{
    private long _observed;
    private long _mutated;

    /// <summary>Observations applied through the store's delta write.</summary>
    public long Observed => Interlocked.Read(ref _observed);

    /// <summary>Whole-profile updates applied through the store's transactional update.</summary>
    public long Mutated => Interlocked.Read(ref _mutated);

    internal void RecordObservation() => Interlocked.Increment(ref _observed);

    internal void RecordMutation() => Interlocked.Increment(ref _mutated);
}

/// <summary>
/// The one place profile state is read and written, and the one place that decides how.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two operations, because there are two shapes of change.</b> An observation is a pure append,
/// so it goes through the store's delta write. A promotion needs a <em>decision</em>, provenance,
/// freeze state, candidate state, so it goes through the store's transactional update, which runs
/// that decision inside the same write lock. Neither is optimistic and neither retries.
///
/// <para>
/// <b>Both are safe under a burst, and the split is not about that.</b> Each takes the write lock
/// before reading, so many writers queue rather than race whichever one is used. What the split buys
/// is that "this is an append" is stated once, in the store, instead of every caller reimplementing
/// the merge inside an update delegate, and that the counters below can tell the two apart.
/// </para>
///
/// <para>
/// <b>This used to be far more machinery.</b> A striped array of per-profile gates, a bounded
/// compare-and-swap retry loop, and a conflict exception, all of it standing in for the two store
/// operations that now exist. The retry loop could not survive a burst, under many concurrent
/// writers only one wins a round and the bounded budget loses the rest, measured at 4 attempts and
/// 4 conflicts against a sustained burst, and the gate only ever covered writers inside this
/// process, which is the case the store already handles. All of it was deleted rather than tuned.
/// </para>
///
/// <para>
/// What remains is not a pass-through: it is the named place where "append, or decide?" is answered,
/// and where the counters that would catch that answer being wrong live.
/// </para>
/// </remarks>
public sealed class ProfileCoordinator
{
    private readonly IAdaptiveProfileStore _store;

    public ProfileCoordinator(IAdaptiveProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Write counters. A get-only property holding a locked object, so the type stays
    /// stateless in the sense the shared-state tripwire checks.</summary>
    public ProfileWriteStatistics Statistics { get; } = new();

    /// <summary>Reads a profile without touching it. Used by the assessment-only path.</summary>
    public AdaptiveProfile Read(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // A profile nobody has seen is returned as an empty one rather than as null, because every
        // caller here wants "what do we know about this sender", and the honest answer for an unseen
        // sender is "nothing", not "I could not answer".
        return _store.Load(key) ?? new AdaptiveProfile(key);
    }

    /// <summary>Folds one attempt into a profile. The burst path.</summary>
    public void Observe(ProfileKey key, ProfileObservation observation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);

        Statistics.RecordObservation();
        _store.ApplyObservation(key, observation, now);
    }

    /// <summary>Applies a change that needs a decision. The rare path.</summary>
    /// <param name="mutate">
    /// Runs inside the store's write lock, holding it for the duration, in-memory work only, and
    /// never a call back into this coordinator.
    /// </param>
    public T Mutate<T>(ProfileKey key, DateTimeOffset now, Func<AdaptiveProfile, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(mutate);

        Statistics.RecordMutation();
        return _store.Update(key, now, mutate);
    }
}
