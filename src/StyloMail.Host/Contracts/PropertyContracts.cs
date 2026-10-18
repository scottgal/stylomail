using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Contracts;

/// <summary>One addressed scope on the wire.</summary>
public sealed record PropertyScopeResponse
{
    public required PropertyScopeKind Kind { get; init; }

    public required string Id { get; init; }

    public static PropertyScopeResponse From(PropertyScope scope) => new()
    {
        Kind = scope.Kind,
        Id = scope.Id,
    };
}

/// <summary>What produced an inferred value.</summary>
public sealed record PropertyModelResponse
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string PromptContract { get; init; }

    public IReadOnlyList<string> Spans { get; init; } = [];

    public static PropertyModelResponse From(ModelContribution model) => new()
    {
        Id = model.Id,
        Version = model.Version,
        PromptContract = model.PromptContract,
        Spans = model.Spans,
    };
}

/// <summary>Whether a contribution exists, is absent, or does not apply.</summary>
/// <remarks>
/// The three states exist so an absent contribution never reads as a value or a zero:
/// <c>Available</c> with a payload, <c>NotProduced</c> with a reason, or <c>NotApplicable</c> with a
/// reason. A client that only checks for null cannot tell "not measured" from "does not apply".
/// </remarks>
public sealed record PropertyAvailabilityResponse
{
    public required AvailabilityState State { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }

    public static PropertyAvailabilityResponse From(PropertyAvailability availability) => new()
    {
        State = availability.State,
        Reason = availability.Reason,
        Detail = availability.Detail,
    };
}

/// <summary>The denominator behind a confidence, or a named reason there is none.</summary>
public sealed record PropertyCoverageResponse
{
    public required CoverageState State { get; init; }

    public int? Analysed { get; init; }

    public int? Total { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }

    public static PropertyCoverageResponse From(PropertyCoverage? coverage, PropertySource? source)
    {
        if (coverage is null)
        {
            return source is PropertySource.Inferred or PropertySource.Import
                ? new PropertyCoverageResponse
                {
                    State = CoverageState.Unavailable,
                    Reason = AvailabilityReasons.CoverageMissing,

                    // An inferred or imported value without coverage is a visible gap, not a
                    // stylistic one: a confidence or a claim is present with nothing behind it.
                    Detail = "No coverage is recorded, so this contribution has no denominator.",
                }
                : new PropertyCoverageResponse
                {
                    State = CoverageState.NotApplicable,
                    Reason = AvailabilityReasons.SourceIsNotEvidenceDerived,
                    Detail = "This value is not evidence-derived, so it has no sample.",
                };
        }

        if (!string.IsNullOrWhiteSpace(coverage.UnavailableReason))
        {
            return new PropertyCoverageResponse
            {
                State = CoverageState.Unavailable,
                Reason = coverage.UnavailableReason,
                Detail = "The producer stated that coverage is unavailable.",
            };
        }

        return new PropertyCoverageResponse
        {
            State = CoverageState.Available,
            Analysed = coverage.Analysed,
            Total = coverage.Total,
            WindowStart = coverage.WindowStart,
            WindowEnd = coverage.WindowEnd,
        };
    }
}

/// <summary>One evidence reference with enough metadata to explain it without content.</summary>
public sealed record PropertyEvidenceRefResponse
{
    public required string Id { get; init; }

    public required EvidenceKind Kind { get; init; }

    public string? Label { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public required EvidenceRetention Retention { get; init; }

    public DateTimeOffset? RetentionUntil { get; init; }

    public required EvidenceAccessOutcome AccessOutcome { get; init; }

    public static PropertyEvidenceRefResponse From(EvidenceReference reference) => new()
    {
        Id = reference.Id,
        Kind = reference.Kind,
        Label = reference.Label,
        ObservedAt = reference.ObservedAt,
        Retention = reference.Retention,
        RetentionUntil = reference.RetentionUntil,
        AccessOutcome = reference.AccessOutcome,
    };
}

/// <summary>A stored row at one scope, with its provenance.</summary>
/// <remarks>
/// Every property response is built from this shape rather than from a bare value. A client that
/// only needs the value still receives the state beside it, so "no value" can never be rendered as
/// a value by accident.
/// </remarks>
public sealed record PropertyRowResponse
{
    public required PropertyScopeResponse Scope { get; init; }

    public required string Value { get; init; }

    public required PropertySurfaceState State { get; init; }

    public required PropertySource Source { get; init; }

    public double? Confidence { get; init; }

    public PropertyModelResponse? Model { get; init; }

    public required PropertyAvailabilityResponse ModelAvailability { get; init; }

    public IReadOnlyList<PropertyEvidenceRefResponse> EvidenceRefs { get; init; } = [];

    public required PropertyCoverageResponse Coverage { get; init; }

    public required string SetBy { get; init; }

    public required DateTimeOffset SetAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ConfirmedBy { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public static PropertyRowResponse From(PropertyRecord record) => new()
    {
        Scope = PropertyScopeResponse.From(record.Scope),
        Value = record.Value,
        State = record.SurfaceState,
        Source = record.Source,
        Confidence = record.Confidence,
        Model = record.Model is null ? null : PropertyModelResponse.From(record.Model),
        ModelAvailability = PropertyAvailabilityResponse.From(
            record.Model is not null
                ? PropertyAvailability.Available
                : record.Source == PropertySource.Inferred
                    ? PropertyAvailability.NotProduced(
                        AvailabilityReasons.ModelContributionMissing,
                        "An inferred value is recorded without a model contribution.")
                    : PropertyAvailability.NotApplicable(
                        AvailabilityReasons.SourceIsNotModelDerived,
                        "This value was not produced by a model.")),
        EvidenceRefs = [.. record.EvidenceRefs.Select(PropertyEvidenceRefResponse.From)],
        Coverage = PropertyCoverageResponse.From(record.Coverage, record.Source),
        SetBy = record.SetBy,
        SetAt = record.SetAt,
        ExpiresAt = record.ExpiresAt,
        ConfirmedBy = record.ConfirmedBy,
        ConfirmedAt = record.ConfirmedAt,
    };
}

/// <summary>One row that was considered, and whether it governed.</summary>
public sealed record PropertyCandidateResponse
{
    public required PropertyRowResponse Row { get; init; }

    /// <summary>This row supplied the effective value.</summary>
    public required bool Selected { get; init; }

    /// <summary>
    /// This row was eligible to compete. False means the key refuses its source, which is how a
    /// stored-but-ineligible value stays visible without governing.
    /// </summary>
    public required bool Eligible { get; init; }

    public static PropertyCandidateResponse From(PropertyCandidate candidate) => new()
    {
        Row = PropertyRowResponse.From(candidate.Record),
        Selected = candidate.Selected,
        Eligible = candidate.Eligible,
    };
}

/// <summary>The effective value of one property at one addressed scope.</summary>
/// <remarks>
/// <b><see cref="State"/> is the pill, and it is derived from the source and the confirmation
/// marker, not from the source word alone.</b> <c>Unknown</c> means no unexpired value exists at
/// any consulted scope: it is not a pill and not a zero, it is the honest absence.
/// </remarks>
public sealed record PropertyResponse
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public required PropertySurfaceState State { get; init; }

    public PropertySource? Source { get; init; }

    /// <summary>The scope the effective value is bound at. Null when unknown.</summary>
    public PropertyScopeResponse? Scope { get; init; }

    public double? Confidence { get; init; }

    public PropertyModelResponse? Model { get; init; }

    public required PropertyAvailabilityResponse ModelAvailability { get; init; }

    public IReadOnlyList<PropertyEvidenceRefResponse> EvidenceRefs { get; init; } = [];

    public required PropertyCoverageResponse Coverage { get; init; }

    public string? SetBy { get; init; }

    public DateTimeOffset? SetAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ConfirmedBy { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>Whether a value exists, and why when it does not.</summary>
    public required PropertyAvailabilityResponse Availability { get; init; }

    /// <summary>
    /// Every unexpired row consulted, narrowest first, the governing one marked. This is how an
    /// override is visible rather than implied.
    /// </summary>
    public IReadOnlyList<PropertyCandidateResponse> Candidates { get; init; } = [];

    public static PropertyResponse From(EffectiveProperty property) => new()
    {
        Key = property.Key,
        Value = property.Value,
        State = property.State,
        Source = property.Source,
        Scope = property.Scope is { } scope ? PropertyScopeResponse.From(scope) : null,
        Confidence = property.Confidence,
        Model = property.Model is null ? null : PropertyModelResponse.From(property.Model),
        ModelAvailability = PropertyAvailabilityResponse.From(property.ModelAvailability),
        EvidenceRefs = [.. property.EvidenceRefs.Select(PropertyEvidenceRefResponse.From)],
        Coverage = PropertyCoverageResponse.From(property.Coverage, property.Source),
        SetBy = property.SetBy,
        SetAt = property.SetAt,
        ExpiresAt = property.ExpiresAt,
        ConfirmedBy = property.ConfirmedBy,
        ConfirmedAt = property.ConfirmedAt,
        Availability = PropertyAvailabilityResponse.From(property.Availability),
        Candidates = [.. property.Candidates.Select(PropertyCandidateResponse.From)],
    };
}

/// <summary>The answer to an effective read.</summary>
/// <remarks>
/// Every requested key is present, including one with nothing recorded. A listing that omitted an
/// unknown key would make "the console did not ask" indistinguishable from "the answer is empty".
/// </remarks>
public sealed record PropertyReadResponse
{
    public required string TenantId { get; init; }

    public required PropertyScopeResponse Scope { get; init; }

    public required DateTimeOffset ResolvedAt { get; init; }

    public required IReadOnlyList<PropertyResponse> Properties { get; init; }
}

/// <summary>The answer to a set, confirm or clear.</summary>
/// <remarks>
/// <see cref="Effective"/> is resolved at the addressed scope alone, not over an inherited chain:
/// a mutation route was not given the chain, and guessing one would report a binding the operator
/// never asked about. A client that needs the inherited view calls the effective read.
/// </remarks>
public sealed record PropertyWriteResponse
{
    /// <summary>The row at the addressed scope afterwards, or null after a successful clear.</summary>
    public PropertyRowResponse? Stored { get; init; }

    public required PropertyResponse Effective { get; init; }

    public static PropertyWriteResponse From(PropertyRecord? stored, EffectiveProperty effective) => new()
    {
        Stored = stored is null ? null : PropertyRowResponse.From(stored),
        Effective = PropertyResponse.From(effective),
    };
}

/// <summary>Who did what, when, and what changed.</summary>
public sealed record PropertyAuditEventResponse
{
    public required string EventId { get; init; }

    public required PropertyAuditAction Action { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public PropertySource? OldSource { get; init; }

    public PropertySource? NewSource { get; init; }

    public string? OldConfirmedBy { get; init; }

    public string? NewConfirmedBy { get; init; }

    public string? Reason { get; init; }

    public static PropertyAuditEventResponse From(PropertyAuditEvent entry) => new()
    {
        EventId = entry.EventId,
        Action = entry.Action,
        Actor = entry.Actor,
        RecordedAt = entry.RecordedAt,
        OldValue = entry.OldValue,
        NewValue = entry.NewValue,
        OldSource = entry.OldSource,
        NewSource = entry.NewSource,
        OldConfirmedBy = entry.OldConfirmedBy,
        NewConfirmedBy = entry.NewConfirmedBy,
        Reason = entry.Reason,
    };
}

/// <summary>The answer to a history read, newest first.</summary>
public sealed record PropertyHistoryResponse
{
    public required string TenantId { get; init; }

    public required PropertyScopeResponse Scope { get; init; }

    public required string Key { get; init; }

    public required IReadOnlyList<PropertyAuditEventResponse> Events { get; init; }
}

/// <summary>The body of a property set.</summary>
/// <remarks>
/// <see cref="Source"/> is a string rather than an enum so an unknown one is refused with a named
/// code instead of a model-binding failure. Omitted means <c>declared</c>: the route requires the
/// administer privilege, so the caller is a human setting a value by hand unless it says otherwise.
/// </remarks>
public sealed record PropertySetRequest
{
    public string? Value { get; init; }

    public string? Source { get; init; }

    public double? Confidence { get; init; }

    public PropertyModelRequest? Model { get; init; }

    public IReadOnlyList<PropertyEvidenceRefRequest>? EvidenceRefs { get; init; }

    public PropertyCoverageRequest? Coverage { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// One evidence reference as a writer supplies it.
/// </summary>
/// <remarks>
/// The writer states the metadata it has; the store records it and does not verify it. An omitted
/// kind is <c>Other</c>, an omitted retention is <c>Unknown</c>, and an omitted access outcome is
/// <c>NotEvaluated</c>: each is the honest answer for a claim nobody made, never an implied
/// "available".
/// </remarks>
public sealed record PropertyEvidenceRefRequest
{
    public string? Id { get; init; }

    public EvidenceKind Kind { get; init; } = EvidenceKind.Other;

    public string? Label { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public EvidenceRetention Retention { get; init; } = EvidenceRetention.Unknown;

    public DateTimeOffset? RetentionUntil { get; init; }

    public EvidenceAccessOutcome AccessOutcome { get; init; } = EvidenceAccessOutcome.NotEvaluated;
}

/// <summary>Coverage as a writer supplies it: counts and window, or a named reason it cannot.</summary>
public sealed record PropertyCoverageRequest
{
    public int? Analysed { get; init; }

    public int? Total { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public string? UnavailableReason { get; init; }
}

/// <summary>The model contribution an inferred write must name.</summary>
public sealed record PropertyModelRequest
{
    public string? Id { get; init; }

    public string? Version { get; init; }

    public string? PromptContract { get; init; }

    public IReadOnlyList<string>? Spans { get; init; }
}

/// <summary>The body of a confirm. The reason is recorded but is not required.</summary>
public sealed record PropertyConfirmRequest
{
    public string? Reason { get; init; }
}

/// <summary>The body of a clear.</summary>
public sealed record PropertyClearRequest
{
    public string? Reason { get; init; }
}
