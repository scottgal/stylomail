namespace StyloMail.Embeddings;

/// <summary>
/// The closed element vocabulary, versioned.
/// </summary>
/// <remarks>
/// <b>A score without a vocabulary version is not comparable, and adding a name is a version bump
/// rather than a quiet sixth string.</b> Consumers validate and store against the version they were
/// told, so an element that appears in a response but not in the version it claims is a contract
/// violation and is reported as one.
/// </remarks>
public static class ElementVocabulary
{
    /// <summary>
    /// The version these names belong to. Elements change here, never in place.
    /// </summary>
    /// <remarks>
    /// <b>elements/2 removed two elements, and that is a vocabulary change rather than a tuning
    /// change.</b> An independent 20-row check by a different model lineage found the teacher labelling
    /// impersonated_greeting and attachment_lure on rows where the acts were not present: the quoted
    /// phrases were real, but they did not contain the act. A label another lineage cannot reproduce is
    /// not evidence, so both are out. credential_request stays PROVISIONAL: the teacher missed it on
    /// three rows the independent check found, and it earns its place only if a 60-row check agrees.
    /// </remarks>
    public const string Version = "elements/2";

    public const string CredentialRequest = "credential_request";
    public const string UrgencyPressure = "urgency_pressure";
    public const string Threat = "threat";

    /// <summary>
    /// Not in elements/2. Retained so stored elements/1 rows remain readable and code that names the
    /// old strings still compiles; nothing in <see cref="All"/> produces them.
    /// </summary>
    public const string ImpersonatedGreeting = "impersonated_greeting";

    /// <summary>
    /// Not in elements/2. Retained so stored elements/1 rows remain readable and code that names the
    /// old strings still compiles; nothing in <see cref="All"/> produces them.
    /// </summary>
    public const string AttachmentLure = "attachment_lure";

    /// <summary>Every element, in the order a response reports them.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        CredentialRequest,
        UrgencyPressure,
        Threat,
    };

    /// <summary>True when a name is one of the five this version defines.</summary>
    public static bool Contains(string? element) =>
        element is not null && ((IReadOnlyList<string>)All).Contains(element, StringComparer.Ordinal);
}

/// <summary>The closed set of scorer names.</summary>
/// <remarks>
/// Two scorers answer different questions. See <see cref="ElementScore.Score"/> for what each
/// number means; they are deliberately not interchangeable and a consumer that blends them is
/// comparing a margin to a probability.
/// </remarks>
public static class ElementScorers
{
    /// <summary>Nearest threatening exemplar minus nearest benign exemplar. A signed margin.</summary>
    public const string PrototypeKnn = "prototype_knn";

    /// <summary>Logistic regression over the same vector. A probability.</summary>
    public const string LinearProbe = "linear_probe";

    /// <summary>
    /// The version the served artefact reports. It moves when the artefact moves, in the same
    /// commit: the store validates a point's version against these constants, so a mismatch is a
    /// point that never lands. The service reports corpus-1 because that is the artefact it serves.
    /// </summary>
    public const string PrototypeKnnVersion = "prototype_knn/corpus-1";

    public const string LinearProbeVersion = "linear_probe/corpus-1";

    public static readonly IReadOnlyList<string> All = new[] { PrototypeKnn, LinearProbe };

    public static bool Contains(string? scorer) =>
        scorer is not null && ((IReadOnlyList<string>)All).Contains(scorer, StringComparer.Ordinal);

    /// <summary>The version string a named scorer reports, or null for an unknown name.</summary>
    public static string? VersionOf(string scorer) => scorer switch
    {
        PrototypeKnn => PrototypeKnnVersion,
        LinearProbe => LinearProbeVersion,
        _ => null,
    };
}

/// <summary>Whether an element's score was produced for a segment.</summary>
public enum ElementAvailability
{
    Available = 0,

    /// <summary>No score. <see cref="ElementScore.Reason"/> names why, and there is never a zero.</summary>
    NotProduced = 1,
}

/// <summary>Why an element score was not produced. Closed, and stored per point.</summary>
public enum ElementNotProducedReason
{
    None = 0,

    /// <summary>No labelled exemplars for this element in the artefact, so no margin exists.</summary>
    NoExemplars,

    /// <summary>The linear probe has no trained model for this element.</summary>
    ProbeNotTrained,

    /// <summary>
    /// The element is in the vocabulary but the artefact carries fewer than the training minimum of
    /// positive exemplars, so no score can be produced. Distinct from <see cref="NoExemplars"/> (none
    /// at all). The operator action differs: this one means enlarge the sample.
    /// </summary>
    InsufficientExemplars,

    /// <summary>The artefact does not carry this element or could not be read.</summary>
    ArtefactMissing,

    Unauthorized,
    ServiceUnavailable,
    TimedOut,
    ModelTagMismatch,
    DimensionMismatch,
    PrefixVersionMismatch,
    UnsupportedDtype,
    ContractViolation,
    TooManySegments,
    SegmentTooLong,
    EmptySegment,
    CircuitOpen,
}

/// <summary>Wire names for <see cref="ElementNotProducedReason"/>.</summary>
public static class ElementReasons
{
    public static string ToWireName(this ElementNotProducedReason reason) => reason switch
    {
        ElementNotProducedReason.None => "none",
        ElementNotProducedReason.NoExemplars => "no_exemplars",
        ElementNotProducedReason.ProbeNotTrained => "probe_not_trained",
        ElementNotProducedReason.InsufficientExemplars => "insufficient_exemplars",
        ElementNotProducedReason.ArtefactMissing => "artefact_missing",
        ElementNotProducedReason.Unauthorized => "unauthorized",
        ElementNotProducedReason.ServiceUnavailable => "service_unavailable",
        ElementNotProducedReason.TimedOut => "timed_out",
        ElementNotProducedReason.ModelTagMismatch => "model_tag_mismatch",
        ElementNotProducedReason.DimensionMismatch => "dimension_mismatch",
        ElementNotProducedReason.PrefixVersionMismatch => "prefix_version_mismatch",
        ElementNotProducedReason.UnsupportedDtype => "unsupported_dtype",
        ElementNotProducedReason.ContractViolation => "contract_violation",
        ElementNotProducedReason.TooManySegments => "too_many_segments",
        ElementNotProducedReason.SegmentTooLong => "segment_too_long",
        ElementNotProducedReason.EmptySegment => "empty_segment",
        ElementNotProducedReason.CircuitOpen => "circuit_open",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no wire name for this reason"),
    };
}

/// <summary>One element's outcome for one segment.</summary>
/// <param name="Element">A name from <see cref="ElementVocabulary"/>.</param>
/// <param name="Score">
/// Null when the point is not produced. For <c>prototype_knn</c> this is a signed margin, the
/// nearest threatening exemplar's cosine similarity minus the nearest benign one, so positive means
/// closer to the threatening set and the value is not a probability. For <c>linear_probe</c> it is a
/// logistic probability in [0, 1]. A consumer must not clamp the margin or treat the probability as
/// a margin.
/// </param>
/// <param name="NearestExemplarId">
/// Opaque identifier of the threatening exemplar that produced a kNN margin, of the form
/// <c>seed-1:credential_request:0007</c>. Null for the probe and for any not produced point. Never
/// text.
/// </param>
public sealed record ElementScore(
    string Element,
    ElementAvailability Availability,
    double? Score,
    string? NearestExemplarId,
    ElementNotProducedReason Reason);

/// <summary>All element scores for one segment.</summary>
public sealed record SegmentElementScores(int Ordinal, IReadOnlyList<ElementScore> Elements);

/// <summary>One scorer's results across the batch.</summary>
public sealed record ElementScorerResult(
    string Scorer,
    string ScorerVersion,
    IReadOnlyList<SegmentElementScores> Segments);

/// <summary>
/// The classification of one batch, with the provenance needed to store the numbers comparably.
/// </summary>
/// <remarks>
/// <see cref="EmbeddingMetadata"/> is null only when no call succeeded; every point then carries a
/// reason and a null score. <see cref="VocabularyVersion"/> is separate from a scorer version on
/// purpose: adding an element is a vocabulary change and does not retrain a probe.
/// </remarks>
public sealed record ElementClassification(
    EmbeddingMetadata? Metadata,
    string VocabularyVersion,
    IReadOnlyList<ElementScorerResult> Scorers);

/// <summary>Why a classification call produced no result at all.</summary>
/// <remarks>
/// A whole-call absence is different from a per-element absence. This enum answers "the analyser
/// could not run"; <see cref="ElementNotProducedReason"/> answers "this element has no measurement".
/// Both are typed so a caller never has to catch an exception to learn which happened.
/// </remarks>
public enum ClassificationUnavailableReason
{
    None = 0,

    /// <summary>No service, token or prototype set is configured, so there is no analyser to ask.</summary>
    NoAnalyserConfigured,

    /// <summary>The service refused the connection, or answered 5xx after the retry.</summary>
    ServiceUnavailable,

    /// <summary>The call exceeded the configured deadline.</summary>
    TimedOut,

    /// <summary>The service rejected the bearer token.</summary>
    Unauthorized,

    /// <summary>The response violated the published contract or a version disagreed.</summary>
    ContractViolation,

    /// <summary>The service's model, dimension or prefix version is not the configured one.</summary>
    UnsupportedModel,

    /// <summary>The request itself was outside the bounds this provider accepts.</summary>
    InvalidRequest,
}

/// <summary>Wire names for <see cref="ClassificationUnavailableReason"/>.</summary>
public static class ClassificationReasons
{
    public static string ToWireName(this ClassificationUnavailableReason reason) => reason switch
    {
        ClassificationUnavailableReason.None => "none",
        ClassificationUnavailableReason.NoAnalyserConfigured => "no_analyser_configured",
        ClassificationUnavailableReason.ServiceUnavailable => "service_unavailable",
        ClassificationUnavailableReason.TimedOut => "timed_out",
        ClassificationUnavailableReason.Unauthorized => "unauthorized",
        ClassificationUnavailableReason.ContractViolation => "contract_violation",
        ClassificationUnavailableReason.UnsupportedModel => "unsupported_model",
        ClassificationUnavailableReason.InvalidRequest => "invalid_request",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no wire name for this reason"),
    };
}

/// <summary>
/// The outcome of one classification call: either a result, or a named reason none was produced.
/// </summary>
/// <remarks>
/// Expected failures are values, not exceptions. A caller that has to catch an exception to build a
/// one-outcome, zero-points row would end up inventing a reason for anything unexpected, and a
/// closed vocabulary with a catch-all is not closed.
/// </remarks>
public abstract record ClassificationOutcome
{
    private ClassificationOutcome()
    {
    }

    /// <summary>A result. It may still carry per-element absences with their own reasons.</summary>
    public sealed record Success(ElementClassification Classification) : ClassificationOutcome;

    /// <summary>No result, with a reason from the closed set.</summary>
    public sealed record Unavailable(ClassificationUnavailableReason Reason) : ClassificationOutcome;
}

/// <summary>
/// Classifies bounded segments into <see cref="ElementVocabulary"/> elements.
/// </summary>
/// <remarks>
/// One row per segment ordinal per element per scorer. Ordinals are the caller's contiguous zero
/// based segmentation ordinals, echoed unchanged; this interface never re-segments and never sees
/// message text beyond the call.
/// </remarks>
public interface IElementClassifier
{
    /// <summary>
    /// Classify a batch. Throws only for programmer errors (a null argument or an unknown scorer
    /// name); every operational failure is returned as <see cref="ClassificationOutcome.Unavailable"/>.
    /// </summary>
    Task<ClassificationOutcome> ClassifyAsync(
        IReadOnlyList<string> segments,
        IReadOnlyList<string>? scorers = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The fixed tone vocabulary: the 28 GoEmotions labels, versioned separately from elements.
/// </summary>
/// <remarks>
/// <b>Tone is a feature series, never a threat verdict.</b> It has its own model tag and vocabulary
/// version so a stored tone point is never compared with a point from another tag, and it is never
/// blended into an element score.
/// </remarks>
public static class ToneModels
{
    /// <summary>
    /// The GoEmotions model this deployment uses, chosen on measured latency and coverage on the
    /// inference GPU: 28-label vocabulary, 6.3 ms median for a 200 word segment, load 1.53 s, top
    /// label at or above 0.50 on 20 of 20 sample segments. The distilroberta arm was 5.4 ms with 7
    /// labels and 0.95 coverage; the latency difference is under a millisecond and the vocabulary is
    /// four times narrower, so the GoEmotions tag is the one. Measurements:
    /// tools/embeddings/training/tone-metrics.json.
    /// </summary>
    public const string GoEmotions = "SamLowe/roberta-base-go_emotions";
}

public static class ToneVocabulary
{
    public const string Version = "goemotions/1";

    public static readonly IReadOnlyList<string> All = new[]
    {
        "admiration", "amusement", "anger", "annoyance", "approval", "caring", "confusion",
        "curiosity", "desire", "disappointment", "disapproval", "disgust", "embarrassment",
        "excitement", "fear", "gratitude", "grief", "joy", "love", "nervousness", "optimism",
        "pride", "realization", "relief", "remorse", "sadness", "surprise", "neutral",
    };

    public static bool Contains(string? label) =>
        label is not null && ((IReadOnlyList<string>)All).Contains(label, StringComparer.Ordinal);
}

/// <summary>Whether a tone series was produced for a segment.</summary>
public enum ToneAvailability
{
    Available = 0,
    NotProduced = 1,
}

/// <summary>Why tone was not produced.</summary>
public enum ToneNotProducedReason
{
    None = 0,
    ToneModelUnavailable,
    ServiceUnavailable,
    TimedOut,
    Unauthorized,
    ContractViolation,
    UnsupportedModel,
}

/// <summary>Wire names for <see cref="ToneNotProducedReason"/>.</summary>
public static class ToneReasons
{
    public static string ToWireName(this ToneNotProducedReason reason) => reason switch
    {
        ToneNotProducedReason.None => "none",
        ToneNotProducedReason.ToneModelUnavailable => "tone_model_unavailable",
        ToneNotProducedReason.ServiceUnavailable => "service_unavailable",
        ToneNotProducedReason.TimedOut => "timed_out",
        ToneNotProducedReason.Unauthorized => "unauthorized",
        ToneNotProducedReason.ContractViolation => "contract_violation",
        ToneNotProducedReason.UnsupportedModel => "unsupported_model",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no wire name for this reason"),
    };
}

/// <summary>One label and its score.</summary>
public sealed record ToneLabelScore(string Label, double Score);

/// <summary>Top-k tone labels for one segment. An empty list with NotProduced is not an empty mood.</summary>
public sealed record SegmentTone(
    int Ordinal,
    ToneAvailability Availability,
    IReadOnlyList<ToneLabelScore> Top,
    ToneNotProducedReason Reason);

/// <summary>
/// A tone series across a batch, carrying its own model tag and vocabulary version.
/// </summary>
/// <remarks>
/// This type is on disk ahead of the model so the consumer can bind a sibling seam, as agreed. The
/// service does not emit it yet.
/// </remarks>
public sealed record ToneSeries(
    string ToneModelTag,
    string ToneVocabularyVersion,
    IReadOnlyList<SegmentTone> Segments);
