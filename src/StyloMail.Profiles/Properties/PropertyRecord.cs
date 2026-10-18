namespace StyloMail.Profiles.Properties;

/// <summary>
/// What produced an inferred value, recorded so the contribution can be audited later.
/// </summary>
/// <remarks>
/// <para>
/// <b>No silent LLM authority.</b> A model contribution that cannot be named by model, version and
/// prompt contract is not storable, because a value nobody can reproduce is a value nobody can
/// defend when an operator asks why a protection applied or did not.
/// </para>
/// <para>
/// <see cref="Spans"/> are the evidence locations the model actually read, in whatever notation the
/// producer uses. They are opaque strings here: the property store does not parse message content,
/// and storing a span is not storing content.
/// </para>
/// </remarks>
public sealed record ModelContribution
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string PromptContract { get; init; }

    public IReadOnlyList<string> Spans { get; init; } = [];
}

/// <summary>
/// One stored property row: a value at exactly one scope, with everything needed to read it
/// honestly.
/// </summary>
public sealed record PropertyRecord
{
    public required PropertyScope Scope { get; init; }

    public required string Key { get; init; }

    public required string Value { get; init; }

    public required PropertySource Source { get; init; }

    /// <summary>
    /// How sure the source was. Required for an inferred value, optional elsewhere.
    /// </summary>
    /// <remarks>
    /// Beside the value rather than inside <see cref="Model"/> because it describes this value's
    /// confidence, and the model block describes which mechanism produced it. Together they are the
    /// "model, version, prompt contract, confidence and spans" the spec requires a contribution to
    /// carry.
    /// </remarks>
    public double? Confidence { get; init; }

    public ModelContribution? Model { get; init; }

    public IReadOnlyList<EvidenceReference> EvidenceRefs { get; init; } = [];

    /// <summary>The denominator behind <see cref="Confidence"/>, when the source has one.</summary>
    public PropertyCoverage? Coverage { get; init; }

    public required string SetBy { get; init; }

    public required DateTimeOffset SetAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Who confirmed it, from the principal rather than from the request body.</summary>
    public string? ConfirmedBy { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>
    /// The pill this row would carry if it governed. Derived, never stored: a stored state is a
    /// second answer that can drift from the source and the confirmation marker.
    /// </summary>
    public PropertySurfaceState SurfaceState => PropertySurfaceStates.For(Source, ConfirmedBy);

    /// <summary>Whether this row is still in force at <paramref name="now"/>.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

/// <summary>
/// A candidate row as it appeared in a resolution, including whether it governed.
/// </summary>
/// <remarks>
/// <b>The binding is always visible.</b> A console that can only show the winning value cannot
/// explain an override, and an operator who cannot see that a conversation narrowed a binding will
/// eventually read the narrowed value as a fact about the entity.
/// </remarks>
public sealed record PropertyCandidate
{
    public required PropertyRecord Record { get; init; }

    /// <summary>This row supplied the effective value.</summary>
    public required bool Selected { get; init; }

    /// <summary>
    /// This row was allowed to compete at all. False for a source the key refuses, which is how a
    /// stored but ineligible value stays visible without governing.
    /// </summary>
    public required bool Eligible { get; init; }
}

/// <summary>
/// The effective value of one property at one scope, with its provenance.
/// </summary>
/// <remarks>
/// <b>A bare value is a contract violation.</b> Every field that explains where the value came from
/// travels with it, including the ones that are absent when nothing is known: an
/// <see cref="PropertySurfaceState.Unknown"/> property states that no unexpired value exists rather
/// than returning a null with no explanation. A caller must never have to distinguish "not
/// measured" from "measured as empty" by guessing.
/// </remarks>
public sealed record EffectiveProperty
{
    public required string Key { get; init; }

    /// <summary>The value, or null when <see cref="State"/> is Unknown.</summary>
    public string? Value { get; init; }

    public required PropertySurfaceState State { get; init; }

    /// <summary>The source that supplied the effective value, or null when unknown.</summary>
    public PropertySource? Source { get; init; }

    /// <summary>The scope the effective value is bound at, or null when unknown.</summary>
    public PropertyScope? Scope { get; init; }

    public double? Confidence { get; init; }

    public ModelContribution? Model { get; init; }

    public IReadOnlyList<EvidenceReference> EvidenceRefs { get; init; } = [];

    /// <summary>The denominator behind <see cref="Confidence"/>, when the source has one.</summary>
    public PropertyCoverage? Coverage { get; init; }

    public string? SetBy { get; init; }

    public DateTimeOffset? SetAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ConfirmedBy { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>Every unexpired row consulted, narrowest first, with the governing one marked.</summary>
    public IReadOnlyList<PropertyCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Whether an effective value exists, and why when it does not.
    /// </summary>
    /// <remarks>
    /// <b>"Not set" is not an explanation.</b> A drawer that cannot say whether nothing was ever
    /// recorded or whether everything recorded was refused cannot make an honest absence claim, so
    /// the state and reason travel with the empty value rather than being inferred from an empty
    /// candidate list.
    /// </remarks>
    public required PropertyAvailability Availability { get; init; }

    /// <summary>
    /// Whether a model contributed, derived from the source and the model block.
    /// </summary>
    /// <remarks>
    /// A missing model contribution on a declared value is <c>not_applicable</c>, not
    /// <c>not_produced</c>: nobody failed to run a model, the value simply was not model-derived.
    /// An inferred value with no model block is <c>not_produced</c>, which is a visible defect rather
    /// than a silent absence.
    /// </remarks>
    public PropertyAvailability ModelAvailability => Model is not null
        ? PropertyAvailability.Available
        : Source == PropertySource.Inferred
            ? PropertyAvailability.NotProduced(
                AvailabilityReasons.ModelContributionMissing,
                "An inferred value is recorded without a model contribution.")
            : PropertyAvailability.NotApplicable(
                AvailabilityReasons.SourceIsNotModelDerived,
                "This value was not produced by a model.");

    /// <summary>The honest absence, carrying the key so a response can always be keyed by it.</summary>
    public static EffectiveProperty Unknown(string key, IReadOnlyList<PropertyCandidate>? candidates = null)
    {
        var considered = candidates ?? [];

        return new EffectiveProperty
        {
            Key = key,
            Value = null,
            State = PropertySurfaceState.Unknown,
            Candidates = considered,
            Availability = considered.Count == 0
                ? PropertyAvailability.NotProduced(
                    AvailabilityReasons.NoUnexpiredValue,
                    "No value is in force for this key at any consulted scope.")
                : PropertyAvailability.NotProduced(
                    AvailabilityReasons.OnlyIneligibleCandidates,
                    "Values are recorded for this key, but none comes from a source this key accepts, "
                    + "so none may govern."),
        };
    }
}
