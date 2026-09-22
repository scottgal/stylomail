using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;

namespace StyloMail.Assessment.Semantic;

/// <summary>
/// A classifier that can be driven from an explicit clock.
/// </summary>
/// <remarks>
/// <see cref="ISemanticMailClassifier"/> is deliberately free of ambient state, which is right, /// but expiry and sampling need to know what time it is, and taking the time from the wall clock
/// would make a replay produce different cache decisions than the run it is replaying. This
/// interface carries the clock in explicitly at the one call site that has it, which is the
/// assessment context. The plain <see cref="ISemanticMailClassifier"/> method still exists for
/// callers that have no context and are content with the default clock.
/// </remarks>
public interface IContextualSemanticClassifier : ISemanticMailClassifier
{
    ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        TimeProvider clock,
        CancellationToken cancellationToken);
}

/// <summary>Counters for the operator surface. Nothing here influences a decision.</summary>
public sealed class SemanticCacheStatistics
{
    private long _hits;
    private long _misses;
    private long _expiries;
    private long _versionInvalidations;
    private long _securityBearingRejections;
    private long _sampledRefreshes;
    private long _providerCalls;
    private long _collapsedJoins;
    private long _staleServed;
    private long _unavailableNotStored;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Expiries => Interlocked.Read(ref _expiries);
    public long VersionInvalidations => Interlocked.Read(ref _versionInvalidations);
    public long SecurityBearingRejections => Interlocked.Read(ref _securityBearingRejections);
    public long SampledRefreshes => Interlocked.Read(ref _sampledRefreshes);
    public long ProviderCalls => Interlocked.Read(ref _providerCalls);
    public long CollapsedJoins => Interlocked.Read(ref _collapsedJoins);
    public long StaleServed => Interlocked.Read(ref _staleServed);

    /// <summary>
    /// Classifications that produced nothing and were therefore not memoised.
    /// </summary>
    /// <remarks>
    /// Tracked because it is the failure this cache most needs to avoid: an outage that gets
    /// written down becomes an outage that lasts as long as the entry's lifetime, and every
    /// message in between reads a cache hit instead of a provider problem.
    /// </remarks>
    public long UnavailableNotStored => Interlocked.Read(ref _unavailableNotStored);

    internal void RecordHit() => Interlocked.Increment(ref _hits);
    internal void RecordMiss() => Interlocked.Increment(ref _misses);
    internal void RecordExpiry() => Interlocked.Increment(ref _expiries);
    internal void RecordVersionInvalidation() => Interlocked.Increment(ref _versionInvalidations);
    internal void RecordSecurityBearingRejection() => Interlocked.Increment(ref _securityBearingRejections);
    internal void RecordSampledRefresh() => Interlocked.Increment(ref _sampledRefreshes);
    internal void RecordProviderCall() => Interlocked.Increment(ref _providerCalls);
    internal void RecordCollapsedJoin() => Interlocked.Increment(ref _collapsedJoins);
    internal void RecordStaleServed() => Interlocked.Increment(ref _staleServed);
    internal void RecordUnavailableNotStored() => Interlocked.Increment(ref _unavailableNotStored);
}

/// <summary>
/// Memoises semantic assessments in front of another classifier.
/// </summary>
/// <remarks>
/// <para>
/// <b>This decorates evidence, not decisions.</b> What it returns is a
/// <see cref="SemanticAssessment"/>, the same type the provider returns, so there is no shape in
/// which a cache hit could carry an action, a disposition, or a permission. "Never memoise allow
/// this sender" is enforced by the contract rather than by discipline: the decorated interface
/// cannot express an allow, so no future edit can store one here.
/// </para>
///
/// <para>
/// Every message whose semantic evidence comes from this cache still receives current
/// deterministic evidence, current authentication and URL checks, current behavioural counters and
/// a current policy decision. The cache removes exactly one thing: a network call to a classifier,
/// for an input that is byte-for-byte the question it already answered.
/// </para>
///
/// <para>
/// <b>An unavailable answer is never stored.</b> Caching an outage would turn a transient provider
/// problem into a lifetime-long one, every message for the next few hours would read as a
/// confident cache hit with provenance that says "hit" rather than "we could not ask". Results that
/// produced nothing are returned but not retained.
/// </para>
/// </remarks>
public sealed class SemanticCacheClassifier : IContextualSemanticClassifier
{
    private readonly ISemanticMailClassifier _inner;
    private readonly ISemanticCacheStore _store;
    private readonly SemanticCacheOptions _options;
    private readonly TimeProvider _defaultClock;
    private readonly SingleFlight<string, SemanticAssessment> _flights = new();

    public SemanticCacheClassifier(
        ISemanticMailClassifier inner,
        ISemanticCacheStore store,
        SemanticCacheOptions options,
        TimeProvider? defaultClock = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _inner = inner;
        _store = store;
        _options = options;
        _defaultClock = defaultClock ?? TimeProvider.System;
    }

    public SemanticCacheStatistics Statistics { get; } = new();

    public ISemanticCacheStore Store => _store;

    public ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        CancellationToken cancellationToken) =>
        ClassifyAsync(input, _defaultClock, cancellationToken);

    public async ValueTask<SemanticAssessment> ClassifyAsync(
        SemanticMailInput input,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var keyDigest = SemanticCacheKey.Digest(input, _options);
        var fingerprint = SecurityBearingFingerprint.Compute(input.Message);

        var lookup = Evaluate(keyDigest, fingerprint, now);

        if (lookup.Entry is { } servable && lookup.Lookup == SemanticCacheLookup.Hit)
        {
            Statistics.RecordHit();
            return ToHit(servable, keyDigest, stale: false);
        }

        var expired = lookup.Lookup == SemanticCacheLookup.Expired ? lookup.Entry : null;

        // Every path below calls the provider, so they all join the same flight key: two concurrent
        // misses on one key, or a miss and a concurrent sampled refresh, cost one provider call.
        var fresh = await _flights
            .RunAsync(keyDigest, () => CallProviderAsync(input, keyDigest, cancellationToken))
            .ConfigureAwait(false);

        if (IsEmptyAnswer(fresh))
        {
            // Nothing was answered, so there is nothing worth remembering. With stale reuse opted
            // into this is the one case that reads the expired entry; otherwise the outage stays an
            // outage all the way through, which is what the invariant requires.
            if (expired is not null && _options.ServeStaleOnProviderUnavailable)
            {
                Statistics.RecordStaleServed();
                return ToHit(expired, keyDigest, stale: true);
            }

            Statistics.RecordUnavailableNotStored();
            return fresh;
        }

        _store.Store(
            new CachedSemanticAssessment
            {
                KeyDigest = keyDigest,
                Evidence = fresh.Evidence,
                Coverage = SemanticCoverage.From(fresh.Evidence),
                CachedAt = now,
                ExpiresAt = now + _options.EntryLifetime,
                ResolvedModelVersion = fresh.ResolvedModelVersion,
                QuestionSchemaVersion = _options.QuestionSchemaVersion,
                PreprocessingVersion = _options.PreprocessingVersion,
                Fingerprint = fingerprint,
                UntrustedMessageIdHeader = input.Message.Envelope.UntrustedMessageIdHeader,
                InputTokens = fresh.InputTokens,
                OutputTokens = fresh.OutputTokens,
            },
            _options,
            now);

        return fresh;
    }

    /// <summary>
    /// Applies expiry, version invalidation and the security-bearing gate, and reports which one
    /// applied. Kept separate so the decision is readable at a glance and testable on its own.
    /// </summary>
    private SemanticCacheEntry Evaluate(
        string keyDigest,
        SecurityBearingFingerprint fingerprint,
        DateTimeOffset now)
    {
        var lookup = _store.Lookup(keyDigest, _options, now);

        switch (lookup.Lookup)
        {
            case SemanticCacheLookup.VersionMismatch:
                Statistics.RecordVersionInvalidation();
                return lookup;

            case SemanticCacheLookup.Expired:
                Statistics.RecordExpiry();
                break;
        }

        if (lookup.Entry is { } candidate
            && !string.Equals(candidate.Fingerprint.Digest, fingerprint.Digest, StringComparison.Ordinal))
        {
            // Unreachable while the key digest covers every field the fingerprint does. Checked
            // anyway: if the two ever disagree, the safe reading is that this is a different
            // message that merely collided, and the entry must not answer for it.
            _store.Remove(keyDigest);
            Statistics.RecordSecurityBearingRejection();
            return new SemanticCacheEntry { Lookup = SemanticCacheLookup.SecurityBearingMismatch };
        }

        if (lookup.Lookup == SemanticCacheLookup.Hit && ShouldReclassify(keyDigest))
        {
            Statistics.RecordSampledRefresh();
            return new SemanticCacheEntry { Lookup = SemanticCacheLookup.Missing };
        }

        return lookup;
    }

    private async Task<SemanticAssessment> CallProviderAsync(
        SemanticMailInput input,
        string keyDigest,
        CancellationToken cancellationToken)
    {
        Statistics.RecordProviderCall();
        var assessment = await _inner.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);

        // The inner classifier's provenance describes its own internals. The caller's ledger should
        // carry one coherent vocabulary, and at this level the answer either came from this cache or
        // it did not.
        return assessment with
        {
            Cache = new CacheProvenance
            {
                Hit = false,
                KeyDigest = keyDigest,
                CachedAt = null,
                ModelVersion = assessment.ResolvedModelVersion,
                Stale = false,
            },
        };
    }

    private static SemanticAssessment ToHit(
        CachedSemanticAssessment entry,
        string keyDigest,
        bool stale) => new()
        {
            // The evidence keeps the timestamps the provider gave it. Re-dating a hit to now would
            // make an hour-old model opinion indistinguishable from a fresh one in the ledger.
            Evidence = entry.Evidence,
            ResolvedModelVersion = entry.ResolvedModelVersion,
            Cache = new CacheProvenance
            {
                Hit = true,
                KeyDigest = keyDigest,
                CachedAt = entry.CachedAt,
                ModelVersion = entry.ResolvedModelVersion,
                Stale = stale,
            },
            InputTokens = entry.InputTokens,
            OutputTokens = entry.OutputTokens,
        };

    /// <summary>True when the provider answered nothing that could be used.</summary>
    private static bool IsEmptyAnswer(SemanticAssessment assessment) =>
        assessment.Evidence.Count == 0
        || assessment.Evidence.All(e => e.Availability
            is EvidenceAvailability.Unavailable or EvidenceAvailability.NotApplicable);

    /// <summary>
    /// Whether this hit should be refreshed anyway, decided deterministically from the key.
    /// </summary>
    /// <remarks>
    /// Derived from the key digest rather than drawn from a random number generator so that a
    /// replay makes the same sampling decisions as the run it replays. Without that, a replayed
    /// decision could differ from the original for a reason that has nothing to do with the
    /// message, which is the one thing a replay must never do.
    /// </remarks>
    private bool ShouldReclassify(string keyDigest)
    {
        if (_options.ReclassificationSampleRate <= 0)
        {
            return false;
        }

        if (_options.ReclassificationSampleRate >= 1)
        {
            return true;
        }

        // Keyed, not salted-and-concatenated: the salt is the key material, so a change to it
        // rotates which keys are sampled without any risk of two salts colliding on one digest.
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.SamplingSalt),
            Encoding.UTF8.GetBytes(keyDigest));

        var sample = BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8)) / (double)ulong.MaxValue;
        return sample < _options.ReclassificationSampleRate;
    }
}
