namespace StyloMail.Policy;

/// <summary>How wide a binding's scope is. Narrower scopes win when several apply.</summary>
public enum BindingScope
{
    Tenant = 0,
    Company = 1,
    Domain = 2,
    Entity = 3,
    Conversation = 4,
}

/// <summary>What a binding authorises or relaxes.</summary>
public enum BindingAction
{
    /// <summary>Permit a sender or entity that a pack would otherwise hold or review.</summary>
    Permit = 0,

    /// <summary>Score and display without acting, for a scope where action is undesired.</summary>
    Observe = 1,

    /// <summary>Permit content capture for the scope, within the pack's declared widest mode.</summary>
    CaptureText = 2,

    /// <summary>Raise a category's tolerance for the scope. Always safe; no second approver needed.</summary>
    RaiseTolerance = 3,

    /// <summary>Lower a category's tolerance for the scope. Never below a hard floor.</summary>
    LowerTolerance = 4,
}

/// <summary>
/// What happens when a binding reaches its expiry.
/// </summary>
/// <remarks>
/// There is deliberately no "keep" member. A binding that outlives its expiry is a permanent
/// exception nobody approved, so expiry always either returns to the pack or holds for review.
/// </remarks>
public enum BindingOnExpiry
{
    /// <summary>Silently return to the pack's declared tolerance.</summary>
    RevertToPack = 0,

    /// <summary>Keep the scope held until a human reviews it. The conservative choice.</summary>
    HoldForReview = 1,
}

/// <summary>
/// A scoped, time-bound manual exception: the route by which an operator's knowledge enters policy
/// without pretending it was measured (spec 9.16).
/// </summary>
/// <remarks>
/// <para>
/// Every field that bounds the exception is non-nullable and required, so forgetting an expiry is a
/// compile error rather than a binding that happens to live forever. The author states the reason;
/// a change of tolerance or a capture grant also names a different approver. The record is the
/// audit: it is never edited, only superseded by a new binding, and revocation is an audit event
/// rather than a deletion.
/// </para>
/// </remarks>
public sealed record PolicyBinding
{
    public required string Id { get; init; }

    public required string TenantId { get; init; }

    public required BindingScope ScopeKind { get; init; }

    /// <summary>The concrete scope value: a tenant id, company id, domain, entity id or conversation id.</summary>
    public required string ScopeValue { get; init; }

    public required BindingAction Action { get; init; }

    /// <summary>The category the tolerance change applies to. Required for tolerance actions.</summary>
    public string? CategoryId { get; init; }

    /// <summary>The level the binding sets. Required for tolerance actions.</summary>
    public ToleranceLevel? TargetLevel { get; init; }

    /// <summary>The pack version this binding was authored against.</summary>
    public string? PackVersion { get; init; }

    public required string Author { get; init; }

    /// <summary>
    /// A second human, required for capture and for lowering a tolerance. A permit or an observe
    /// binding may be authored alone, because neither can make the deployment less protective than
    /// the pack it sits beside.
    /// </summary>
    public string? ApprovedBy { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The date a human must look at the binding again. Never after the expiry.</summary>
    public required DateTimeOffset ReviewBy { get; init; }

    public required BindingOnExpiry OnExpiry { get; init; }

    public bool IsActiveAt(DateTimeOffset instant) => instant >= StartsAt && instant < ExpiresAt;

    /// <summary>
    /// What this binding resolves to. Returned as a value rather than applied in place, so the
    /// caller can record which binding was effective without mutating the pack.
    /// </summary>
    public BindingResolution Resolve() => new()
    {
        BindingId = Id,
        Action = Action,
        CategoryId = CategoryId,
        TargetLevel = TargetLevel,
    };

    /// <summary>The immutable audit record for a lifecycle event on this binding.</summary>
    public BindingAuditEntry Audit(BindingAuditEvent @event, string actor, DateTimeOffset at, string? reason = null) => new()
    {
        BindingId = Id,
        TenantId = TenantId,
        Event = @event,
        Actor = actor,
        At = at,
        Reason = reason ?? Reason,
    };
}

/// <summary>What a binding means once resolved.</summary>
public sealed record BindingResolution
{
    public required string BindingId { get; init; }

    public required BindingAction Action { get; init; }

    public string? CategoryId { get; init; }

    public ToleranceLevel? TargetLevel { get; init; }
}

/// <summary>Lifecycle events that are recorded rather than applied by deleting a row.</summary>
public enum BindingAuditEvent
{
    Created = 0,
    Reviewed = 1,
    Revoked = 2,
    Expired = 3,
}

/// <summary>An append-only audit row for a binding.</summary>
public sealed record BindingAuditEntry
{
    public required string BindingId { get; init; }

    public required string TenantId { get; init; }

    public required BindingAuditEvent Event { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Reason { get; init; }
}

/// <summary>One reason a binding was refused.</summary>
public sealed record BindingViolation
{
    public required string Code { get; init; }

    public required string Field { get; init; }

    public required string Message { get; init; }
}

/// <summary>The validator's answer.</summary>
public sealed record BindingValidation
{
    public required IReadOnlyList<BindingViolation> Violations { get; init; }

    public bool IsValid => Violations.Count == 0;
}

/// <summary>
/// Validates a binding before it can take effect.
/// </summary>
/// <remarks>
/// <para>
/// The two invariants that matter most are structural elsewhere and checked here:
/// <b>no binding reaches below a hard floor</b>, and <b>no capture grant is wider than its pack</b>.
/// A binding is how manual control enters the system, so it is also the most attractive bypass; the
/// validator refuses a tolerance lowering that has no second approver and a capture grant with no
/// bound or no pack allowance.
/// </para>
/// <para>
/// When a pack is supplied, the binding must belong to the same tenant and may not widen capture
/// beyond the pack's declared mode. Without the pack, the tenant and capture checks cannot be made
/// and are not pretended: the binding is validated on its own terms and the caller is responsible
/// for the pack comparison.
/// </para>
/// </remarks>
public static class PolicyBindingValidator
{
    public const string IdMissing = "binding.id_missing";
    public const string TenantMissing = "binding.tenant_missing";
    public const string ScopeMissing = "binding.scope_missing";
    public const string AuthorMissing = "binding.author_missing";
    public const string ReasonMissing = "binding.reason_missing";
    public const string ExpiryNotAfterStart = "binding.expiry_not_after_start";
    public const string ReviewOutsideWindow = "binding.review_outside_window";
    public const string CategoryMissing = "binding.category_missing";
    public const string CategoryUnknown = "binding.category_unknown";
    public const string TargetLevelMissing = "binding.target_level_missing";
    public const string HardFloorBelowStrict = "binding.hard_floor_below_strict";
    public const string SecondApproverMissing = "binding.second_approver_missing";
    public const string SecondApproverMustDiffer = "binding.second_approver_must_differ";
    public const string CaptureExceedsPack = "binding.capture_exceeds_pack";
    public const string TenantMismatch = "binding.tenant_mismatch";

    public static BindingValidation Validate(PolicyBinding binding, PolicyPack? pack = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var violations = new List<BindingViolation>();

        Require(binding.Id, IdMissing, nameof(PolicyBinding.Id), "A binding needs a stable id.", violations);
        Require(binding.TenantId, TenantMissing, nameof(PolicyBinding.TenantId), "A binding must belong to a tenant.", violations);
        Require(binding.ScopeValue, ScopeMissing, nameof(PolicyBinding.ScopeValue), "A scoped binding must name its scope value.", violations);
        Require(binding.Author, AuthorMissing, nameof(PolicyBinding.Author), "A binding must name its author.", violations);
        Require(binding.Reason, ReasonMissing, nameof(PolicyBinding.Reason), "A binding must state the operator knowledge it encodes.", violations);

        if (binding.ExpiresAt <= binding.StartsAt)
        {
            violations.Add(Violation(ExpiryNotAfterStart, nameof(PolicyBinding.ExpiresAt), "A binding must expire after it starts; there is no unbounded binding."));
        }

        if (binding.ReviewBy < binding.StartsAt || binding.ReviewBy > binding.ExpiresAt)
        {
            violations.Add(Violation(ReviewOutsideWindow, nameof(PolicyBinding.ReviewBy), "The review date must fall inside the binding's own window."));
        }

        if (binding.Action is BindingAction.RaiseTolerance or BindingAction.LowerTolerance)
        {
            ValidateTolerance(binding, violations);
        }

        if (binding.Action is BindingAction.LowerTolerance or BindingAction.CaptureText)
        {
            if (string.IsNullOrWhiteSpace(binding.ApprovedBy))
            {
                violations.Add(Violation(SecondApproverMissing, nameof(PolicyBinding.ApprovedBy), $"{binding.Action} requires a second named approver."));
            }
            else if (string.Equals(binding.ApprovedBy, binding.Author, StringComparison.Ordinal))
            {
                violations.Add(Violation(SecondApproverMustDiffer, nameof(PolicyBinding.ApprovedBy), "The approver must be a different human from the author."));
            }
        }

        if (pack is not null)
        {
            if (!string.Equals(binding.TenantId, pack.TenantId, StringComparison.Ordinal))
            {
                violations.Add(Violation(TenantMismatch, nameof(PolicyBinding.TenantId), "A binding may only modify a pack in its own tenant."));
            }

            if (binding.Action == BindingAction.CaptureText
                && pack.CaptureText is { } capture
                && capture.Mode == TextCaptureMode.None)
            {
                violations.Add(Violation(CaptureExceedsPack, nameof(PolicyBinding.Action), "The pack permits no content capture, so no binding may grant it."));
            }
        }

        return new BindingValidation { Violations = violations };
    }

    private static void ValidateTolerance(PolicyBinding binding, List<BindingViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(binding.CategoryId))
        {
            violations.Add(Violation(CategoryMissing, nameof(PolicyBinding.CategoryId), $"{binding.Action} requires a category."));
            return;
        }

        if (!PolicyCategories.TryGet(binding.CategoryId, out var category))
        {
            violations.Add(Violation(CategoryUnknown, nameof(PolicyBinding.CategoryId), $"'{binding.CategoryId}' is not a policy category."));
            return;
        }

        if (binding.TargetLevel is null)
        {
            violations.Add(Violation(TargetLevelMissing, nameof(PolicyBinding.TargetLevel), $"{binding.Action} requires a target level."));
            return;
        }

        if (binding.Action == BindingAction.LowerTolerance
            && category.IsHardFloor
            && binding.TargetLevel < PolicyCategories.HardFloorMinimum)
        {
            violations.Add(Violation(
                HardFloorBelowStrict,
                nameof(PolicyBinding.TargetLevel),
                $"'{category.Label}' is a hard floor and a binding may not lower it below {PolicyCategories.HardFloorMinimum}."));
        }
    }

    private static void Require(
        string? value,
        string code,
        string field,
        string message,
        List<BindingViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(Violation(code, field, message));
        }
    }

    private static BindingViolation Violation(string code, string field, string message) => new()
    {
        Code = code,
        Field = field,
        Message = message,
    };
}
