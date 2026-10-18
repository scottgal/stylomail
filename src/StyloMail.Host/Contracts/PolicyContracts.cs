using StyloMail.Core;
using StyloMail.Host.Policy;
using StyloMail.Policy;

namespace StyloMail.Host.Contracts;

/// <summary>A draft pack as the author submits it. Tenant, author and approver never come from the body.</summary>
public sealed record PolicyPackDraftRequest
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required PolicyPackContext Context { get; init; }

    public required IReadOnlyDictionary<string, ToleranceLevel> CategoryLevels { get; init; }

    public required PolicyVerdict ActionCeiling { get; init; }

    public required TextCaptureRules CaptureText { get; init; }

    public required int EvidenceRetentionDays { get; init; }

    /// <summary>Review deadline. Omitted means the 24-hour default.</summary>
    public TimeSpan? ReviewDeadline { get; init; }

    public required string RuleSetVersion { get; init; }

    public required string ScorerVersion { get; init; }

    public required DateTimeOffset EffectiveFrom { get; init; }

    public DateTimeOffset? ReviewDue { get; init; }

    public string? Supersedes { get; init; }
}

/// <summary>One pack version with its lifecycle state and payload.</summary>
public sealed record PolicyPackResponse
{
    public required string TenantId { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required PolicyPackState State { get; init; }

    public required PolicyPackContext Context { get; init; }

    public required IReadOnlyDictionary<string, ToleranceLevel> CategoryLevels { get; init; }

    public required PolicyVerdict ActionCeiling { get; init; }

    public required TextCaptureRules CaptureText { get; init; }

    public required int EvidenceRetentionDays { get; init; }

    public required TimeSpan ReviewDeadline { get; init; }

    public required string RuleSetVersion { get; init; }

    public required string ScorerVersion { get; init; }

    public required string Author { get; init; }

    public string? Approver { get; init; }

    public string? SecondApprover { get; init; }

    public required DateTimeOffset EffectiveFrom { get; init; }

    public DateTimeOffset? ReviewDue { get; init; }

    public string? Supersedes { get; init; }

    public required string ContentSha256 { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? SupersededBy { get; init; }

    public static PolicyPackResponse From(PolicyPack pack, PolicyPackSummary summary) => new()
    {
        TenantId = summary.TenantId,
        Id = pack.Id,
        Version = pack.Version,
        State = summary.State,
        Context = pack.Context,
        CategoryLevels = pack.CategoryLevels,
        ActionCeiling = pack.ActionCeiling,
        CaptureText = pack.CaptureText,
        EvidenceRetentionDays = pack.EvidenceRetentionDays,
        ReviewDeadline = pack.ReviewDeadline,
        RuleSetVersion = pack.RuleSetVersion,
        ScorerVersion = pack.ScorerVersion,
        Author = pack.Author,
        Approver = summary.Approver,
        SecondApprover = summary.SecondApprover,
        EffectiveFrom = pack.EffectiveFrom,
        ReviewDue = pack.ReviewDue,
        Supersedes = pack.Supersedes,
        ContentSha256 = summary.ContentSha256,
        CreatedAt = summary.CreatedAt,
        ApprovedAt = summary.ApprovedAt,
        SupersededBy = summary.SupersededBy,
    };
}

/// <summary>A pack version in a list: enough to choose one, never enough to decide with.</summary>
public sealed record PolicyPackSummaryResponse
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required PolicyPackState State { get; init; }

    public required string Jurisdiction { get; init; }

    public required string ContentSha256 { get; init; }

    public required string Author { get; init; }

    public string? Approver { get; init; }

    public string? SecondApprover { get; init; }

    public DateTimeOffset? EffectiveFrom { get; init; }

    public DateTimeOffset? ReviewDue { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? SupersededBy { get; init; }

    public static PolicyPackSummaryResponse From(PolicyPackSummary summary) => new()
    {
        Id = summary.PackId,
        Version = summary.Version,
        State = summary.State,
        Jurisdiction = summary.Jurisdiction,
        ContentSha256 = summary.ContentSha256,
        Author = summary.Author,
        Approver = summary.Approver,
        SecondApprover = summary.SecondApprover,
        EffectiveFrom = summary.EffectiveFrom,
        ReviewDue = summary.ReviewDue,
        CreatedAt = summary.CreatedAt,
        ApprovedAt = summary.ApprovedAt,
        SupersededBy = summary.SupersededBy,
    };
}

/// <summary>The tenant's pack versions, newest first.</summary>
public sealed record PolicyPackListResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<PolicyPackSummaryResponse> Packs { get; init; }
}

/// <summary>The authenticated principal is the approver; this names only the second one.</summary>
public sealed record PolicyPackApprovalRequest
{
    /// <summary>
    /// A second authenticated principal with Administer, required when a category is lowered or
    /// capture is widened. Must be a different principal from the approver.
    /// </summary>
    public string? SecondApprover { get; init; }
}

/// <summary>Why a pack is being withdrawn or deactivated. Recorded in the audit event.</summary>
public sealed record PolicyPackTransitionRequest
{
    public string? Reason { get; init; }
}

/// <summary>A binding as an administrator submits it.</summary>
public sealed record PolicyBindingRequest
{
    public required BindingScope ScopeKind { get; init; }

    public required string ScopeValue { get; init; }

    public required BindingAction Action { get; init; }

    public string? CategoryId { get; init; }

    public ToleranceLevel? TargetLevel { get; init; }

    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public string? ApprovedBy { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset ReviewBy { get; init; }

    public required BindingOnExpiry OnExpiry { get; init; }
}

/// <summary>One binding with its state at the instant it was read.</summary>
public sealed record PolicyBindingResponse
{
    public required string Id { get; init; }

    public required PolicyBindingState State { get; init; }

    public required BindingScope ScopeKind { get; init; }

    public required string ScopeValue { get; init; }

    public required BindingAction Action { get; init; }

    public string? CategoryId { get; init; }

    public ToleranceLevel? TargetLevel { get; init; }

    public required string PackId { get; init; }

    public required string PackVersion { get; init; }

    public required string Author { get; init; }

    public string? ApprovedBy { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset ReviewBy { get; init; }

    public required BindingOnExpiry OnExpiry { get; init; }

    public static PolicyBindingResponse From(PolicyBindingRecord record) => new()
    {
        Id = record.Binding.Id,
        State = record.State,
        ScopeKind = record.Binding.ScopeKind,
        ScopeValue = record.Binding.ScopeValue,
        Action = record.Binding.Action,
        CategoryId = record.Binding.CategoryId,
        TargetLevel = record.Binding.TargetLevel,
        PackId = record.Binding.PackId ?? string.Empty,
        PackVersion = record.Binding.PackVersion ?? string.Empty,
        Author = record.Binding.Author,
        ApprovedBy = record.Binding.ApprovedBy,
        Reason = record.Binding.Reason,
        StartsAt = record.Binding.StartsAt,
        ExpiresAt = record.Binding.ExpiresAt,
        ReviewBy = record.Binding.ReviewBy,
        OnExpiry = record.Binding.OnExpiry,
    };
}

/// <summary>Bindings visible to the caller, newest first.</summary>
public sealed record PolicyBindingListResponse
{
    public required string TenantId { get; init; }

    public required IReadOnlyList<PolicyBindingResponse> Bindings { get; init; }
}

/// <summary>The static action capability catalogue. Not tenant-specific and not a decision.</summary>
public sealed record PolicyCapabilitiesResponse
{
    public required IReadOnlyList<PolicyActionCapability> Capabilities { get; init; }
}
