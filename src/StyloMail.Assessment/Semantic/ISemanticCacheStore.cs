namespace StyloMail.Assessment.Semantic;

/// <summary>Why a lookup did not produce a servable entry.</summary>
/// <remarks>
/// The distinctions matter for the ledger and for the tests. "Nothing was stored under this key"
/// and "something was stored, and it is not the same message" are different facts, and folding
/// them into one miss would hide the second, which is the one that catches a changed bank account.
/// </remarks>
public enum SemanticCacheLookup
{
    /// <summary>No entry is retained for this key.</summary>
    Missing = 0,

    /// <summary>An entry exists and may be served.</summary>
    Hit = 1,

    /// <summary>An entry exists for this key but is past its lifetime.</summary>
    Expired = 2,

    /// <summary>The entry was produced under a different model, question schema or preprocessing version.</summary>
    VersionMismatch = 3,

    /// <summary>
    /// The entry is keyed to this input but describes a message with different security-bearing
    /// properties.
    /// </summary>
    /// <remarks>
    /// This should be unreachable while the key digest is correct, the digest covers every field
    /// the fingerprint does. It is checked anyway, because the cost of being wrong is a changed
    /// payment destination inheriting a previously-assessed verdict, and the cost of checking is
    /// one string comparison.
    /// </remarks>
    SecurityBearingMismatch = 4,
}

/// <summary>One lookup result: the outcome, and the entry when there is one to inspect.</summary>
public sealed record SemanticCacheEntry
{
    public required SemanticCacheLookup Lookup { get; init; }

    public CachedSemanticAssessment? Entry { get; init; }

    /// <summary>True when the entry may be served to the caller.</summary>
    public bool IsServable => Lookup == SemanticCacheLookup.Hit && Entry is not null;
}

/// <summary>
/// Retains memoised semantic assessments under their exact-key digest.
/// </summary>
/// <remarks>
/// Implementations bound their own memory and are safe for concurrent use; the decorator above
/// makes no attempt to serialise access. Single-flight collapsing is a separate concern
/// (<see cref="SingleFlight{TKey, TResult}"/>) because it is about concurrent <em>calls</em>,
/// not about stored values.
/// </remarks>
public interface ISemanticCacheStore
{
    /// <summary>
    /// Looks up an entry, applying expiry and version invalidation.
    /// </summary>
    /// <remarks>
    /// The comparison against <paramref name="options"/> is done here rather than by the caller so
    /// that no future call site can forget it: an entry carrying a different resolved model, a
    /// different question schema or a different preprocessing version is reported as a mismatch and
    /// never as a hit.
    /// </remarks>
    SemanticCacheEntry Lookup(string keyDigest, SemanticCacheOptions options, DateTimeOffset now);

    void Store(CachedSemanticAssessment entry, SemanticCacheOptions options, DateTimeOffset now);

    /// <summary>
    /// Discards one entry.
    /// </summary>
    /// <remarks>
    /// Used when an entry is known to be unusable rather than merely unhelpful, a key collision
    /// whose security-bearing fingerprint disagrees. Leaving it would mean re-reading a wrong
    /// entry on every subsequent message that hashes to the same key.
    /// </remarks>
    void Remove(string keyDigest);

    /// <summary>Current retained count. Exposed for the operator surface and for tests.</summary>
    int Count { get; }
}

/// <summary>
/// A bounded, in-process cache with expiry and configurable LRU/LFU eviction.
/// </summary>
/// <remarks>
/// Deliberately process-local and deliberately lossy. Losing an entry costs one provider call;
/// keeping it forever costs memory that belongs to the message pipeline, and a semantic cache is
/// an optimisation over a network hop rather than a system of record. Durable retention of the
/// <em>evidence</em> belongs in the decision ledger, which is a different component with different
/// retention rules, conflating them would put message-derived content in a cache whose eviction
/// policy was chosen for throughput.
///
/// <para>
/// Expired entries are kept until something needs the space. They are never served as hits, but
/// they remain readable for the one case that asks for them explicitly: an operator who has opted
/// into stale-on-outage reuse.
/// </para>
/// </remarks>
public sealed class InMemorySemanticCacheStore : ISemanticCacheStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedSemanticAssessment> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _recency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private long _tick;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public SemanticCacheEntry Lookup(string keyDigest, SemanticCacheOptions options, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDigest);
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            if (!_entries.TryGetValue(keyDigest, out var entry))
            {
                return new SemanticCacheEntry { Lookup = SemanticCacheLookup.Missing };
            }

            // Version invalidation is checked before expiry, and removes the entry. An answer from a
            // previous model is not an old version of this answer, it is a different answer, and
            // reporting it as merely expired would suggest waiting would fix it.
            if (!IsVersionCompatible(entry, options))
            {
                RemoveLocked(keyDigest);
                return new SemanticCacheEntry { Lookup = SemanticCacheLookup.VersionMismatch };
            }

            Touch(keyDigest);

            return entry.IsExpiredAt(now)
                ? new SemanticCacheEntry { Lookup = SemanticCacheLookup.Expired, Entry = entry }
                : new SemanticCacheEntry { Lookup = SemanticCacheLookup.Hit, Entry = entry };
        }
    }

    public void Store(CachedSemanticAssessment entry, SemanticCacheOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            // Replacing an existing key is not growth, so the eviction pass only runs on a new key.
            // Without this, a hot key would trigger a full scan on every refresh.
            var isNew = !_entries.ContainsKey(entry.KeyDigest);

            _entries[entry.KeyDigest] = entry;
            _recency[entry.KeyDigest] = ++_tick;
            _hits[entry.KeyDigest] = _hits.GetValueOrDefault(entry.KeyDigest);

            if (!isNew)
            {
                return;
            }

            while (_entries.Count > options.MaxEntries && _entries.Count > 0)
            {
                EvictOne(options.EvictionPolicy);
            }
        }
    }

    public void Remove(string keyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDigest);

        lock (_gate)
        {
            RemoveLocked(keyDigest);
        }
    }

    /// <summary>Forgets everything. Used by tests and by an explicit operator reset.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _hits.Clear();
        }
    }

    private static bool IsVersionCompatible(CachedSemanticAssessment entry, SemanticCacheOptions options) =>
        string.Equals(entry.QuestionSchemaVersion, options.QuestionSchemaVersion, StringComparison.Ordinal)
        && string.Equals(entry.PreprocessingVersion, options.PreprocessingVersion, StringComparison.Ordinal)
        // The resolved model must equal the configured one. An entry whose provider reported some
        // other model was produced under a classifier that has since moved, exactly the alias-drift
        // case, and is never served. An absent resolved model cannot be confirmed either, so it
        // fails the same check rather than passing on the benefit of the doubt.
        && string.Equals(entry.ResolvedModelVersion, options.ClassifierModelVersion, StringComparison.Ordinal);

    private void Touch(string keyDigest)
    {
        _recency[keyDigest] = ++_tick;
        _hits[keyDigest] = _hits.GetValueOrDefault(keyDigest) + 1;
    }

    private void EvictOne(SemanticCacheEvictionPolicy policy)
    {
        string? victim = null;

        if (policy == SemanticCacheEvictionPolicy.LeastFrequentlyUsed)
        {
            var bestHits = int.MaxValue;
            var bestRecency = long.MaxValue;

            foreach (var key in _entries.Keys)
            {
                var hits = _hits.GetValueOrDefault(key);
                var recency = _recency.GetValueOrDefault(key);

                // Fewest hits wins; ties go to the least recently used, so equally-frequent keys
                // still drain instead of the eviction pass picking the same victim forever.
                if (hits < bestHits || (hits == bestHits && recency < bestRecency))
                {
                    bestHits = hits;
                    bestRecency = recency;
                    victim = key;
                }
            }
        }
        else
        {
            var bestRecency = long.MaxValue;

            foreach (var key in _entries.Keys)
            {
                var recency = _recency.GetValueOrDefault(key);
                if (recency < bestRecency)
                {
                    bestRecency = recency;
                    victim = key;
                }
            }
        }

        if (victim is not null)
        {
            RemoveLocked(victim);
        }
    }

    private void RemoveLocked(string keyDigest)
    {
        _entries.Remove(keyDigest);
        _recency.Remove(keyDigest);
        _hits.Remove(keyDigest);
    }
}
