namespace StyloMail.Desktop.Api.Contracts;

/// <summary>The tenant's authoritative policy status.</summary>
public sealed record PolicyStatusResponse
{
    public required string TenantId { get; init; }

    public required bool AuthoritativeEnabled { get; init; }

    public string? Jurisdiction { get; init; }

    public string? EffectivePackId { get; init; }

    public string? EffectivePackVersion { get; init; }

    public required bool ShadowEnabled { get; init; }

    public required bool ReviewStateAvailable { get; init; }

    public string? CacheAge { get; init; }
}

public sealed record PolicyPackListResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<PolicyPackSummaryRow> Packs { get; init; }
}

public sealed record PolicyPackSummaryRow
{
    public string? Id { get; init; }
    public string? Version { get; init; }
    public string? State { get; init; }
    public string? Jurisdiction { get; init; }
    public string? ContentSha256 { get; init; }
    public string? Author { get; init; }
    public string? Approver { get; init; }
    public string? SecondApprover { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? ReviewDue { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? SupersededBy { get; init; }
}

public sealed record PolicyPackResponse
{
    public string? TenantId { get; init; }
    public string? Id { get; init; }
    public string? Version { get; init; }
    public string? State { get; init; }
    public PolicyPackContextRow? Context { get; init; }
    public IReadOnlyDictionary<string, string> CategoryLevels { get; init; } = new Dictionary<string, string>();
    public string? ActionCeiling { get; init; }
    public TextCaptureRulesRow? CaptureText { get; init; }
    public int? EvidenceRetentionDays { get; init; }
    public string? ReviewDeadline { get; init; }
    public string? RuleSetVersion { get; init; }
    public string? ScorerVersion { get; init; }
    public string? Author { get; init; }
    public string? Approver { get; init; }
    public string? SecondApprover { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? ReviewDue { get; init; }
    public string? Supersedes { get; init; }
    public string? ContentSha256 { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? SupersededBy { get; init; }
}

public sealed record PolicyPackContextRow
{
    public string? Vertical { get; init; }
    public string? Audience { get; init; }
    public string? Jurisdiction { get; init; }
}

public sealed record TextCaptureRulesRow
{
    public string? Mode { get; init; }
    public int? MaxCharacters { get; init; }
    public int? RetentionDays { get; init; }
    public string? Statement { get; init; }
}

public sealed record PolicyBindingListResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<PolicyBindingRow> Bindings { get; init; }
}

public sealed record PolicyBindingRow
{
    public string? Id { get; init; }
    public string? State { get; init; }
    public string? ScopeKind { get; init; }
    public string? ScopeValue { get; init; }
    public string? Action { get; init; }
    public string? CategoryId { get; init; }
    public string? TargetLevel { get; init; }
    public string? PackId { get; init; }
    public string? PackVersion { get; init; }
    public string? Author { get; init; }
    public string? ApprovedBy { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ReviewBy { get; init; }
    public string? OnExpiry { get; init; }
}
