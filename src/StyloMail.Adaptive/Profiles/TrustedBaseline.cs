using StyloMail.Adaptive.Scoring;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// Where a training label came from. Provenance decides whether a label may teach anything.
/// </summary>
/// <remarks>
/// The ordering is deliberate: the first three cannot promote a baseline at any scope.
/// "It was delivered", "it got a reply" and "nobody complained" are all things an attacker
/// generates by simply sending mail, so treating them as approval hands the attacker the
/// training loop.
/// </remarks>
public enum LabelProvenance
{
    /// <summary>No authenticated source. Never promotes.</summary>
    Unauthenticated = 0,

    /// <summary>Inferred from successful delivery. Never promotes.</summary>
    DeliveryOnly = 1,

    /// <summary>Inferred from the absence of a complaint. Never promotes.</summary>
    AbsenceOfComplaint = 2,

    /// <summary>A recipient's own preference. Promotes that recipient's preference, never global truth.</summary>
    RecipientPreference = 3,

    /// <summary>An authenticated operator or reviewer decision.</summary>
    AuthenticatedOperator = 4,

    /// <summary>An authoritative application outcome.</summary>
    ApplicationOutcome = 5,

    /// <summary>An explicitly configured, authorized learning rule.</summary>
    AuthorizedRule = 6,
}

/// <summary>Outcome of attempting to teach a profile.</summary>
public enum PromotionOutcome
{
    Promoted = 0,

    /// <summary>The label's provenance carries no training authority.</summary>
    RejectedUntrustedProvenance = 1,

    /// <summary>The label is trusted but not in this scope: a recipient preference is not global truth.</summary>
    RejectedProvenanceOutOfScope = 2,

    /// <summary>The baseline is frozen because compromise is suspected.</summary>
    RejectedBaselineFrozen = 3,

    /// <summary>Recorded against a regime candidate rather than the incumbent baseline.</summary>
    CandidateRecorded = 4,
}

/// <summary>An approved sample offered to a profile as training.</summary>
public sealed record TrustedSample
{
    public required DimensionVector Dimensions { get; init; }

    public required LabelProvenance Provenance { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Free-form label detail for the ledger. Never carries message content.</summary>
    public string? Label { get; init; }
}

/// <summary>Which labels may teach which scopes.</summary>
public static class BaselineLabelPolicy
{
    /// <summary>True when the provenance is a trusted source at all.</summary>
    public static bool IsTrustedProvenance(LabelProvenance provenance) => provenance switch
    {
        LabelProvenance.Unauthenticated => false,
        LabelProvenance.DeliveryOnly => false,
        LabelProvenance.AbsenceOfComplaint => false,
        LabelProvenance.RecipientPreference => true,
        LabelProvenance.AuthenticatedOperator => true,
        LabelProvenance.ApplicationOutcome => true,
        LabelProvenance.AuthorizedRule => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Unknown label provenance."),
    };

    /// <summary>
    /// True when a trusted label of this provenance may teach this scope.
    /// </summary>
    /// <remarks>
    /// Feedback is scoped. A recipient saying "I wanted that promotion" changes that
    /// recipient's preference and nothing else: least of all whether the same content is
    /// phishing when it arrives at somebody who did not ask for it.
    /// </remarks>
    public static bool AppliesToScope(LabelProvenance provenance, ProfileScopeKind scope) => provenance switch
    {
        LabelProvenance.RecipientPreference => scope == ProfileScopeKind.Recipient,
        LabelProvenance.AuthenticatedOperator => true,
        LabelProvenance.ApplicationOutcome => true,
        LabelProvenance.AuthorizedRule => true,
        _ => false,
    };
}

/// <summary>
/// Approved samples only: the distribution of behaviour we are willing to call legitimate.
/// </summary>
/// <remarks>
/// This is the second of the two stores, and the two are never conflated. Everything in here
/// passed a provenance gate; nothing arrives here merely by having been sent.
/// </remarks>
public sealed record TrustedBaseline
{
    public static TrustedBaseline Empty { get; } = new()
    {
        Version = 0,
        TrustedSupport = 0,
        Dimensions = new Dictionary<string, RunningMoments>(StringComparer.Ordinal),
        IsFrozen = false,
    };

    /// <summary>Advances on promotion. Rollback restores a version; it does not erase history.</summary>
    public required int Version { get; init; }

    public required int TrustedSupport { get; init; }

    public required IReadOnlyDictionary<string, RunningMoments> Dimensions { get; init; }

    /// <summary>
    /// Derived view of <see cref="Dimensions"/>. <see langword="null"/> until something is trusted.
    /// </summary>
    public RobustScaleModel? ScaleModel { get; init; }

    public string? RegimeId { get; init; }

    /// <summary>True while compromise is suspected: observed traffic still accrues, learning does not.</summary>
    public required bool IsFrozen { get; init; }

    public string? FreezeReason { get; init; }

    public DateTimeOffset? FrozenAt { get; init; }

    public DateTimeOffset? LastPromotedAt { get; init; }

    /// <summary>
    /// Folds one approved sample into the running moments.
    /// </summary>
    /// <param name="maxMeanShiftFactor">
    /// Largest movement one sample may cause, as a multiple of the dimension's own spread.
    /// <see langword="null"/> learns the sample at face value, which is what a regime candidate
    /// wants: a candidate is gated by support and stability instead, and rate-limiting it would
    /// only slow the evaluation down.
    /// </param>
    public TrustedBaseline WithSample(TrustedSample sample, DateTimeOffset at, double? maxMeanShiftFactor = null)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var dimensions = new Dictionary<string, RunningMoments>(Dimensions, StringComparer.Ordinal);

        foreach (var dimension in sample.Dimensions.Dimensions)
        {
            if (dimension.Value is null)
            {
                // A masked dimension contributes nothing. Treating it as a zero would let a
                // provider outage quietly drag the trusted mean toward zero.
                continue;
            }

            dimensions[dimension.DimensionId] = dimensions.TryGetValue(dimension.DimensionId, out var existing)
                ? existing.Add(dimension.Value.Value, MeanShiftLimit(dimension.DimensionId, maxMeanShiftFactor))
                : RunningMoments.Empty.Add(dimension.Value.Value);
        }

        return this with
        {
            Version = Version + 1,
            TrustedSupport = TrustedSupport + 1,
            Dimensions = dimensions,
            LastPromotedAt = at,
        };
    }

    /// <summary>Rebuilds the derived scale model against the current moments.</summary>
    /// <remarks>
    /// A baseline with no moments has no model at all rather than an empty one, so
    /// <c>ScaleModel is null</c> stays a reliable test for "nothing is trusted here": the
    /// cold-start condition callers must not confuse with "compared, and unremarkable".
    /// </remarks>
    public TrustedBaseline WithRebuiltScale(RobustScaleOptions options) =>
        this with
        {
            ScaleModel = Dimensions.Count == 0 ? null : RobustScaleModel.FromMoments(Dimensions, options),
        };

    /// <summary>
    /// How far one sample may move a dimension's mean, or unbounded when nothing is known yet.
    /// </summary>
    private double MeanShiftLimit(string dimensionId, double? maxMeanShiftFactor)
    {
        if (maxMeanShiftFactor is not { } factor
            || ScaleModel is null
            || !ScaleModel.Dimensions.TryGetValue(dimensionId, out var scale))
        {
            // No established spread means there is nothing for the sample to be far from.
            return double.PositiveInfinity;
        }

        return factor * scale.Scale;
    }
}
