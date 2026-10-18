using StyloMail.Core;
using StyloMail.Profiles.Kpis;
using StyloMail.Profiles.Properties;

namespace StyloMail.Host.Contracts;

/// <summary>One versioned KPI definition on the wire, with the words a reader sees.</summary>
public sealed record KpiDefinitionResponse
{
    public required string Id { get; init; }

    public required string Group { get; init; }

    public required string Label { get; init; }

    public required string Question { get; init; }

    public required string Numerator { get; init; }

    public required string Denominator { get; init; }

    public required string Window { get; init; }

    public required int MinimumSample { get; init; }

    public required KpiDirection Direction { get; init; }

    public required KpiUnit Unit { get; init; }

    public required IReadOnlyList<string> AvailabilityReasons { get; init; }

    public required string Version { get; init; }

    public static KpiDefinitionResponse From(KpiDefinition definition) => new()
    {
        Id = definition.Id,
        Group = definition.Group,
        Label = definition.Label,
        Question = definition.Question,
        Numerator = definition.Numerator,
        Denominator = definition.Denominator,
        Window = definition.Window,
        MinimumSample = definition.MinimumSample,
        Direction = definition.Direction,
        Unit = definition.Unit,
        AvailabilityReasons = definition.AvailabilityReasons,
        Version = definition.Version,
    };
}

/// <summary>The answer to <c>GET /v1/kpis</c>: the definition catalogue, no tenant data.</summary>
public sealed record KpiCatalogueResponse
{
    public required IReadOnlyList<KpiDefinitionResponse> Definitions { get; init; }
}

/// <summary>How much of the population a KPI value was computed from.</summary>
public sealed record KpiCoverageResponse
{
    public required int Analysed { get; init; }

    public required int Total { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required bool Truncated { get; init; }

    public string? Reason { get; init; }

    /// <summary>Observations excluded because their window has not elapsed (responsiveness).</summary>
    public int? Censored { get; init; }

    /// <summary>Outbound candidate rows read (responsiveness).</summary>
    public int? CandidateCount { get; init; }

    /// <summary>The candidate read hit its cap; unmatched messages were excluded, not counted.</summary>
    public bool? CandidatesTruncated { get; init; }

    /// <summary>Candidate rows whose payload could not be read.</summary>
    public int? CandidateUnparsable { get; init; }

    /// <summary>Fully observed messages excluded because the candidate set was incomplete.</summary>
    public int? IncompleteExclusions { get; init; }

    public static KpiCoverageResponse From(KpiCoverage coverage) => new()
    {
        Analysed = coverage.Analysed,
        Total = coverage.Total,
        WindowStart = coverage.WindowStart,
        WindowEnd = coverage.WindowEnd,
        Truncated = coverage.Truncated,
        Reason = coverage.Reason,
        Censored = coverage.Censored,
        CandidateCount = coverage.CandidateCount,
        CandidatesTruncated = coverage.CandidatesTruncated,
        CandidateUnparsable = coverage.CandidateUnparsable,
        IncompleteExclusions = coverage.IncompleteExclusions,
    };
}

/// <summary>A secondary observation served beside a KPI.</summary>
public sealed record KpiMeasurementResponse
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required KpiUnit Unit { get; init; }

    public double? Value { get; init; }

    public string? Reason { get; init; }

    public static KpiMeasurementResponse From(KpiMeasurement measurement) => new()
    {
        Id = measurement.Id,
        Label = measurement.Label,
        Unit = measurement.Unit,
        Value = measurement.Value,
        Reason = measurement.Reason,
    };
}

/// <summary>A prior-window comparison. Never a restatement of definition.direction.</summary>
public sealed record KpiTrendResponse
{
    public required KpiTrendState State { get; init; }

    public double? PriorValue { get; init; }

    public double? Delta { get; init; }

    public double? RelativeDelta { get; init; }

    public KpiTrendMovement? Movement { get; init; }

    public required DateTimeOffset PriorWindowStart { get; init; }

    public required DateTimeOffset PriorWindowEnd { get; init; }

    public required bool PriorTruncated { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }

    public static KpiTrendResponse From(KpiTrend trend) => new()
    {
        State = trend.State,
        PriorValue = trend.PriorValue,
        Delta = trend.Delta,
        RelativeDelta = trend.RelativeDelta,
        Movement = trend.Movement,
        PriorWindowStart = trend.PriorWindowStart,
        PriorWindowEnd = trend.PriorWindowEnd,
        PriorTruncated = trend.PriorTruncated,
        Reason = trend.Reason,
        Detail = trend.Detail,
    };
}

/// <summary>One KPI: its definition, its state, its value and its coverage together.</summary>
public sealed record KpiValueResponse
{
    public required KpiDefinitionResponse Definition { get; init; }

    public required KpiState State { get; init; }

    public double? Value { get; init; }

    public int? Numerator { get; init; }

    public int? Denominator { get; init; }

    public required KpiCoverageResponse Coverage { get; init; }

    public string? Reason { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<KpiMeasurementResponse> Measurements { get; init; } = [];

    /// <summary>Prior-window comparison. Null only where no window was read (the catalogue).</summary>
    public KpiTrendResponse? Trend { get; init; }

    public static KpiValueResponse From(KpiValue value) => new()
    {
        Definition = KpiDefinitionResponse.From(value.Definition),
        State = value.State,
        Value = value.Value,
        Numerator = value.Numerator,
        Denominator = value.Denominator,
        Coverage = KpiCoverageResponse.From(value.Coverage),
        Reason = value.Reason,
        Detail = value.Detail,
        Measurements = [.. value.Measurements.Select(KpiMeasurementResponse.From)],
        Trend = value.Trend is null ? null : KpiTrendResponse.From(value.Trend),
    };
}

/// <summary>One identity observed around the profile, with no address and no content.</summary>
public sealed record ProfileIdentityResponse
{
    public required string Kind { get; init; }

    public required string Value { get; init; }

    public required int Messages { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public static ProfileIdentityResponse From(ProfileIdentity identity) => new()
    {
        Kind = identity.Kind,
        Value = identity.Value,
        Messages = identity.Messages,
        FirstSeenAt = identity.FirstSeenAt,
        LastSeenAt = identity.LastSeenAt,
    };
}

/// <summary>What the profile query could see, and what it could not.</summary>
public sealed record ProfileCoverageResponse
{
    public required int Messages { get; init; }

    public required int TenantMessages { get; init; }

    public required bool Truncated { get; init; }

    public required int Unparsable { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public string? Reason { get; init; }

    public static ProfileCoverageResponse From(ProfileCoverage coverage) => new()
    {
        Messages = coverage.Messages,
        TenantMessages = coverage.TenantMessages,
        Truncated = coverage.Truncated,
        Unparsable = coverage.Unparsable,
        WindowStart = coverage.WindowStart,
        WindowEnd = coverage.WindowEnd,
        FirstMessageAt = coverage.FirstMessageAt,
        LastMessageAt = coverage.LastMessageAt,
        Reason = coverage.Reason,
    };
}

/// <summary>The relationship summary: counts and instants only.</summary>
public sealed record ProfileRelationshipResponse
{
    public DateTimeOffset? FirstSeenAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public int? InboundMessages { get; init; }

    public int? OutboundMessages { get; init; }

    public string? DirectionReason { get; init; }

    public int? DistinctCounterparties { get; init; }

    public string? DistinctCounterpartiesReason { get; init; }

    public int? Conversations { get; init; }

    public string? ConversationsReason { get; init; }

    public static ProfileRelationshipResponse From(ProfileRelationship relationship) => new()
    {
        FirstSeenAt = relationship.FirstSeenAt,
        LastSeenAt = relationship.LastSeenAt,
        InboundMessages = relationship.InboundMessages,
        OutboundMessages = relationship.OutboundMessages,
        DirectionReason = relationship.DirectionReason,
        DistinctCounterparties = relationship.DistinctCounterparties,
        DistinctCounterpartiesReason = relationship.DistinctCounterpartiesReason,
        Conversations = relationship.Conversations,
        ConversationsReason = relationship.ConversationsReason,
    };
}

/// <summary>Something a reader might expect a profile to show, and why it does not.</summary>
public sealed record ProfileNotMeasuredResponse
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Reason { get; init; }

    public static ProfileNotMeasuredResponse From(ProfileNotMeasured entry) => new()
    {
        Id = entry.Id,
        Label = entry.Label,
        Reason = entry.Reason,
    };
}

/// <summary>The answer to <c>GET /v1/profiles/{kind}/{id}</c>.</summary>
public sealed record ProfileResponse
{
    public required string TenantId { get; init; }

    public required PropertyScopeResponse Scope { get; init; }

    /// <summary>A stored human label, or null. Never derived from content or a hash.</summary>
    public string? DisplayLabel { get; init; }

    /// <summary>False when no stored label exists; render "Entity &lt;opaque&gt;".</summary>
    public required bool DisplayLabelAvailable { get; init; }

    /// <summary><c>sender_profile</c>, <c>company</c> or <c>domain</c>, or null when unavailable.</summary>
    public string? DisplayLabelSource { get; init; }

    /// <summary>A named reason when unavailable (<c>no_stored_label</c>).</summary>
    public string? DisplayLabelReason { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required ProfileCoverageResponse Coverage { get; init; }

    public IReadOnlyList<ProfileIdentityResponse> Identities { get; init; } = [];

    public required ProfileRelationshipResponse Relationship { get; init; }

    public required IReadOnlyList<KpiValueResponse> Kpis { get; init; }

    public IReadOnlyList<ProfileNotMeasuredResponse> NotMeasured { get; init; } = [];

    public static ProfileResponse From(string tenantId, ProfileSnapshot snapshot) => new()
    {
        TenantId = tenantId,
        Scope = PropertyScopeResponse.From(snapshot.Scope),
        DisplayLabel = snapshot.DisplayLabel,
        DisplayLabelAvailable = snapshot.DisplayLabelAvailable,
        DisplayLabelSource = snapshot.DisplayLabelSource,
        DisplayLabelReason = snapshot.DisplayLabelReason,
        WindowStart = snapshot.WindowStart,
        WindowEnd = snapshot.WindowEnd,
        Coverage = ProfileCoverageResponse.From(snapshot.Coverage),
        Identities = [.. snapshot.Identities.Select(ProfileIdentityResponse.From)],
        Relationship = ProfileRelationshipResponse.From(snapshot.Relationship),
        Kpis = [.. snapshot.Kpis.Select(KpiValueResponse.From)],
        NotMeasured = [.. snapshot.NotMeasured.Select(ProfileNotMeasuredResponse.From)],
    };
}

/// <summary>How one underlying signal behaved in the evidence window.</summary>
public sealed record KpiSignalEvidenceResponse
{
    public required string SignalId { get; init; }

    public required string Label { get; init; }

    public required int Answered { get; init; }

    public required int Confirming { get; init; }

    public int? Unavailable { get; init; }

    public int? NotApplicable { get; init; }

    public required int Missing { get; init; }

    public string? Reason { get; init; }

    public static KpiSignalEvidenceResponse From(Profiles.KpiSignalEvidence signal) => new()
    {
        SignalId = signal.SignalId,
        Label = signal.Label,
        Answered = signal.Answered,
        Confirming = signal.Confirming,
        Unavailable = signal.Unavailable,
        NotApplicable = signal.NotApplicable,
        Missing = signal.Missing,
        Reason = signal.Reason,
    };
}

/// <summary>An opaque source row behind a KPI, with metadata and no content.</summary>
public sealed record EvidenceSourceRowResponse
{
    public required string AssessmentId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public MailDirection? Direction { get; init; }

    public required MailAction Action { get; init; }

    public IReadOnlyList<string> MeasuredSignalIds { get; init; } = [];

    public static EvidenceSourceRowResponse From(Profiles.EvidenceSourceRow source) => new()
    {
        AssessmentId = source.AssessmentId,
        ObservedAt = source.ObservedAt,
        Direction = source.Direction,
        Action = source.Action,
        MeasuredSignalIds = source.MeasuredSignalIds,
    };
}

/// <summary>The evidence behind one KPI: definition, value, prior value, signals and sources.</summary>
public sealed record KpiEvidenceResponse
{
    public required string TenantId { get; init; }

    public required PropertyScopeResponse Scope { get; init; }

    public required DateTimeOffset ResolvedAt { get; init; }

    public required KpiDefinitionResponse Definition { get; init; }

    public required KpiValueResponse Value { get; init; }

    public required KpiValueResponse PriorValue { get; init; }

    public IReadOnlyList<KpiSignalEvidenceResponse> Signals { get; init; } = [];

    public IReadOnlyList<EvidenceSourceRowResponse> Sources { get; init; } = [];

    public required int SourceCount { get; init; }

    public required bool SourcesTruncated { get; init; }

    public static KpiEvidenceResponse From(
        string tenantId,
        PropertyScopeResponse scope,
        DateTimeOffset resolvedAt,
        Profiles.KpiEvidenceRead evidence) => new()
    {
        TenantId = tenantId,
        Scope = scope,
        ResolvedAt = resolvedAt,
        Definition = KpiDefinitionResponse.From(evidence.Current.Definition),
        Value = KpiValueResponse.From(evidence.Current),
        PriorValue = KpiValueResponse.From(evidence.Prior),
        Signals = [.. evidence.Signals.Select(KpiSignalEvidenceResponse.From)],
        Sources = [.. evidence.Sources.Select(EvidenceSourceRowResponse.From)],
        SourceCount = evidence.SourceCount,
        SourcesTruncated = evidence.SourcesTruncated,
    };
}

/// <summary>One property row that cites an evidence reference.</summary>
public sealed record ReferenceCitationResponse
{
    public required PropertyScopeResponse Scope { get; init; }

    public required string Key { get; init; }

    public required string Value { get; init; }

    public required PropertySource Source { get; init; }

    public required PropertySurfaceState State { get; init; }

    public required string SetBy { get; init; }

    public required DateTimeOffset SetAt { get; init; }

    public string? ConfirmedBy { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public required EvidenceAccessOutcome AccessOutcome { get; init; }

    public required EvidenceRetention Retention { get; init; }

    public DateTimeOffset? RetentionUntil { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public static ReferenceCitationResponse From(Profiles.ReferenceCitation citation) => new()
    {
        Scope = PropertyScopeResponse.From(citation.Scope),
        Key = citation.Key,
        Value = citation.Value,
        Source = citation.Source,
        State = citation.State,
        SetBy = citation.SetBy,
        SetAt = citation.SetAt,
        ConfirmedBy = citation.ConfirmedBy,
        ConfirmedAt = citation.ConfirmedAt,
        AccessOutcome = citation.AccessOutcome,
        Retention = citation.Retention,
        RetentionUntil = citation.RetentionUntil,
        ObservedAt = citation.ObservedAt,
    };
}

/// <summary>Whether the cited source row still exists in this tenant's ledger.</summary>
public sealed record ReferenceSourceResponse
{
    public required string AssessmentId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public MailAction? Action { get; init; }

    public static ReferenceSourceResponse From(Profiles.ReferenceSource source) => new()
    {
        AssessmentId = source.AssessmentId,
        ObservedAt = source.ObservedAt,
        Action = source.Action,
    };
}

/// <summary>The resolved property evidence reference: metadata, citations and source existence.</summary>
public sealed record ReferenceMetadataResponse
{
    public required string Id { get; init; }

    public required EvidenceKind Kind { get; init; }

    /// <summary>The claim's observed time, or null when the writer did not state one.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    public required EvidenceRetention Retention { get; init; }

    public DateTimeOffset? RetentionUntil { get; init; }

    public required EvidenceAccessOutcome AccessOutcome { get; init; }

    /// <remarks>
    /// <b>The writer-supplied <c>label</c> is deliberately not projected here.</b> A label is freeform
    /// text a writer controls, so it could carry an address, a subject or a filename; this route's
    /// content-free guarantee cannot be enforced over it. The property drawer still shows its own
    /// labels against the property row, where the writer's context is explicit.
    /// </remarks>
    public static ReferenceMetadataResponse From(EvidenceReference reference) => new()
    {
        Id = reference.Id,
        Kind = reference.Kind,
        ObservedAt = reference.ObservedAt,
        Retention = reference.Retention,
        RetentionUntil = reference.RetentionUntil,
        AccessOutcome = reference.AccessOutcome,
    };
}

public sealed record ReferenceEvidenceResponse
{
    public required string TenantId { get; init; }

    /// <summary>The newest citation's claim. Explicitly a claim, not "the" reference.</summary>
    public required ReferenceMetadataResponse LatestClaim { get; init; }

    /// <summary>Always <c>all</c>: every matched citation was returned, up to the bound.</summary>
    public required string Selection { get; init; }

    public required Profiles.ReferenceOutcome Outcome { get; init; }

    public required string? OutcomeReason { get; init; }

    public IReadOnlyList<ReferenceCitationResponse> Citations { get; init; } = [];

    /// <summary>Total matching citations, independent of the returned page.</summary>
    public required int CitationCount { get; init; }

    /// <summary>Rows returned in this page.</summary>
    public required int ReturnedCount { get; init; }

    public required bool CitationsTruncated { get; init; }

    /// <summary>Rows whose stored evidence array could not be parsed. Named, never a SQL failure.</summary>
    public required int InvalidCitationRows { get; init; }

    public ReferenceSourceResponse? Source { get; init; }

    /// <summary>Whether the cited source row exists. False is not the same as "not checked".</summary>
    public required bool SourcePresent { get; init; }

    public static ReferenceEvidenceResponse From(
        string tenantId,
        Profiles.ReferenceEvidenceRead evidence) => new()
    {
        TenantId = tenantId,
        LatestClaim = ReferenceMetadataResponse.From(evidence.LatestClaim),
        Selection = evidence.Selection,
        Outcome = evidence.Outcome,
        OutcomeReason = evidence.OutcomeReason,
        Citations = [.. evidence.Citations.Select(ReferenceCitationResponse.From)],
        CitationCount = evidence.CitationCount,
        ReturnedCount = evidence.ReturnedCount,
        CitationsTruncated = evidence.CitationsTruncated,
        InvalidCitationRows = evidence.InvalidCitationRows,
        Source = evidence.Source is null ? null : ReferenceSourceResponse.From(evidence.Source),
        SourcePresent = evidence.SourcePresent,
    };
}
