using StyloMail.Profiles.Kpis;

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

    public static KpiCoverageResponse From(KpiCoverage coverage) => new()
    {
        Analysed = coverage.Analysed,
        Total = coverage.Total,
        WindowStart = coverage.WindowStart,
        WindowEnd = coverage.WindowEnd,
        Truncated = coverage.Truncated,
        Reason = coverage.Reason,
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
        WindowStart = snapshot.WindowStart,
        WindowEnd = snapshot.WindowEnd,
        Coverage = ProfileCoverageResponse.From(snapshot.Coverage),
        Identities = [.. snapshot.Identities.Select(ProfileIdentityResponse.From)],
        Relationship = ProfileRelationshipResponse.From(snapshot.Relationship),
        Kpis = [.. snapshot.Kpis.Select(KpiValueResponse.From)],
        NotMeasured = [.. snapshot.NotMeasured.Select(ProfileNotMeasuredResponse.From)],
    };
}
