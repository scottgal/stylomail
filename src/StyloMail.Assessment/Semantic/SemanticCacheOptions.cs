namespace StyloMail.Assessment.Semantic;

/// <summary>How the semantic cache chooses a victim when it is at capacity.</summary>
/// <remarks>
/// Both policies are offered because they fail differently. LRU is the right default for a
/// stream of broadly-similar traffic: what mattered a moment ago is the best guess at what
/// matters now. LFU survives a flood, a burst of a thousand near-identical lures will not
/// sweep out the frequently-reused entry, but it pins stale entries for as long as they keep
/// being hit, so it is the wrong default for traffic whose vocabulary drifts.
/// </remarks>
public enum SemanticCacheEvictionPolicy
{
    /// <summary>Evict the least recently used entry. The default.</summary>
    LeastRecentlyUsed = 0,

    /// <summary>Evict the least frequently used entry, breaking ties by recency.</summary>
    LeastFrequentlyUsed = 1,
}

/// <summary>
/// Tuning for the semantic cache decorator.
/// </summary>
/// <remarks>
/// <b>This cache stores assessments, never permissions.</b> It memoises one thing, /// <see cref="StyloMail.Core.SemanticAssessment"/>, which is evidence, and it is structurally
/// incapable of returning an action, because the decorated contract
/// (<see cref="StyloMail.Core.ISemanticMailClassifier"/>) cannot express one. There is no
/// configuration here that makes "allow this sender" memoizable, because there is no code path
/// that could store it.
/// </remarks>
public sealed record SemanticCacheOptions
{
    /// <summary>
    /// Version of the classifier's model, as configured, never an alias.
    /// </summary>
    /// <remarks>
    /// Part of the cache key, and re-checked against the model the provider reports it actually
    /// resolved. An alias like <c>jev-latest</c> moves without notice, and a cached assessment
    /// produced by a different model is not the same evidence; serving it would silently change
    /// classifier behaviour underneath tuned thresholds.
    /// </remarks>
    public required string ClassifierModelVersion { get; init; }

    /// <summary>Version of the semantic question set. A change makes cached answers incomparable.</summary>
    public string QuestionSchemaVersion { get; init; } = StyloMail.Core.SemanticDimensions.QuestionSchemaVersion;

    /// <summary>
    /// Version of the preprocessing that produced the classifier input.
    /// </summary>
    /// <remarks>
    /// The same model asked about differently-preprocessed content is a different question. This
    /// stamp is what makes a preprocessing change, say, changing how links are normalised, or
    /// how much body is included, invalidate the corpus rather than quietly mixing two regimes.
    /// </remarks>
    public string PreprocessingVersion { get; init; } = "assessment-preprocessing/1";

    /// <summary>How long an entry may be served. Must be positive.</summary>
    public TimeSpan EntryLifetime { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Maximum retained entries. A bound is a feature here, not tuning, memory is finite.</summary>
    public int MaxEntries { get; init; } = 10_000;

    public SemanticCacheEvictionPolicy EvictionPolicy { get; init; } = SemanticCacheEvictionPolicy.LeastRecentlyUsed;

    /// <summary>
    /// Fraction of would-be hits deliberately reclassified anyway, in [0, 1].
    /// </summary>
    /// <remarks>
    /// A cache with no refresh path only ever holds the first answer it ever computed for a
    /// piece of content, for as long as content repeats. Sampling is how drift gets noticed: a
    /// small fraction of hits are sent to the provider regardless, the entry is replaced, and a
    /// model or prompt change that alters verdicts shows up as a rising disagreement rate rather
    /// than as a silent, permanent opinion.
    ///
    /// <para>
    /// The decision is derived from the key digest and <see cref="SamplingSalt"/> rather than from
    /// a random number generator, so a replay of the same traffic makes the same sampling
    /// decisions. A cache whose behaviour cannot be reproduced cannot be used to reproduce a
    /// decision, which is the point of the whole exercise.
    /// </para>
    /// </remarks>
    public double ReclassificationSampleRate { get; init; }

    /// <summary>
    /// Salt mixed into the deterministic sampling decision.
    /// </summary>
    /// <remarks>
    /// Without it, sampling sweeps the same keys in the same order every deployment, so the same
    /// fraction of the corpus is refreshed and the rest is never touched at all. Changing the salt
    /// rotates which entries get sampled. It is not a secret and carries no authority.
    /// </remarks>
    public string SamplingSalt { get; init; } = "semantic-cache-sampling/1";

    /// <summary>
    /// Serve an expired entry when the provider comes back <em>unavailable</em>.
    /// </summary>
    /// <remarks>
    /// <b>Defaults to <see langword="false"/>, and turning it on is a deliberate downgrade of a
    /// safety property.</b> With it off, a provider outage propagates as
    /// <see cref="StyloMail.Core.EvidenceAvailability.Unavailable"/> all the way through
    /// composition, which is what the invariant requires: unknown stays unknown, and policy sees
    /// reduced coverage rather than a confident answer from an hour ago. With it on, an outage
    /// is converted into old evidence presented as current, and the only place that fact is
    /// visible is <see cref="StyloMail.Core.CacheProvenance.Stale"/>.
    ///
    /// <para>
    /// It exists because an operator may prefer degraded-but-explainable reuse over a pipeline
    /// that drops to unavailable for every message during a provider incident, that is a
    /// legitimate operational call. It is not a safe default, and the decision is recorded on
    /// every assessment it touches rather than being absorbed into the wiring.
    /// </para>
    /// </remarks>
    public bool ServeStaleOnProviderUnavailable { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ClassifierModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(QuestionSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(PreprocessingVersion);

        if (EntryLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(EntryLifetime), EntryLifetime, "Entry lifetime must be positive.");
        }

        if (MaxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), MaxEntries, "The cache must be able to hold at least one entry.");
        }

        if (ReclassificationSampleRate is < 0 or > 1 || double.IsNaN(ReclassificationSampleRate))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReclassificationSampleRate), ReclassificationSampleRate, "The sample rate must be within [0, 1].");
        }
    }
}
