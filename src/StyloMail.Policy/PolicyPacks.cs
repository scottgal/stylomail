using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>
/// How much attention a risk category receives in a deployment.
/// </summary>
/// <remarks>
/// <para>
/// Tolerance is configured per category, never as one global sensitivity dial (spec 9.15). The
/// numeric order is the order of protection, and the code depends on it: a validator that compares
/// two levels is asking "is this at least as protective as that", and a level that sorts out of
/// order would silently answer a different question.
/// </para>
/// <para>
/// <see cref="Off"/> is the only level that means the category is not measured at all. It is
/// deliberately representable, because a commerce deployment may switch off a category that does
/// not apply to it, and deliberately unavailable to the hard floors, which
/// <see cref="PolicyPackValidator"/> refuses rather than silently clamping.
/// </para>
/// </remarks>
public enum ToleranceLevel
{
    /// <summary>Not scored and not shown. Permitted only outside hard floors.</summary>
    Off = 0,

    /// <summary>Scored and displayed, no action.</summary>
    Observe = 1,

    /// <summary>Automatic tier 1 and 2 actions; tier 3 escalates.</summary>
    Standard = 2,

    /// <summary>Lower thresholds, automatic hold, fast human review.</summary>
    Strict = 3,

    /// <summary>
    /// Every candidate escalates to a human immediately, evidence preserved. It means zero
    /// tolerance for <em>unreviewed</em> evidence, never an automatic accusation.
    /// </summary>
    ZeroTolerance = 4,
}

/// <summary>One risk category the policy surface knows about.</summary>
/// <param name="Id">Stable wire id. Renaming it is a migration, not an edit.</param>
/// <param name="Label">Operator-facing name. Never parsed.</param>
/// <param name="IsHardFloor">
/// True when the category may not be lowered below <see cref="ToleranceLevel.Strict"/> by
/// configuration.
/// </param>
public sealed record PolicyCategory(string Id, string Label, bool IsHardFloor);

/// <summary>
/// The closed catalogue of risk categories.
/// </summary>
/// <remarks>
/// <para>
/// An id that is not here is rejected by <see cref="PolicyPackValidator"/>. That is the point: a
/// pack that spells a category the engine does not evaluate would otherwise declare a tolerance for
/// traffic nothing measures, and the operator would read "Strict" where the answer is "not scored".
/// </para>
/// <para>
/// The hard floors are the three named by spec 9.15: child safety and grooming, credible threats of
/// violence, and illegal content. Hard-floor status is a property of the category in code, not a
/// field a pack may set, because a pack that could declare its own floors would let a configuration
/// edit undo a protection.
/// </para>
/// </remarks>
public static class PolicyCategories
{
    public const string ChildSafetyGrooming = "child_safety_grooming";
    public const string CredibleThreatsOfViolence = "credible_threats_of_violence";
    public const string IllegalContent = "illegal_content";
    public const string CoercionAndExtortion = "coercion_and_extortion";
    public const string AdultContent = "adult_content";
    public const string Harassment = "harassment";
    public const string Impersonation = "impersonation";
    public const string CredentialRequest = "credential_request";
    public const string PaymentRedirection = "payment_redirection";
    public const string MalwareAndAttachments = "malware_and_attachments";
    public const string Promotion = "promotion";
    public const string UnsolicitedMail = "unsolicited_mail";

    /// <summary>Every category, in a stable order for display and for canonical output.</summary>
    public static readonly IReadOnlyList<PolicyCategory> All =
    [
        new(ChildSafetyGrooming, "Child safety and grooming", IsHardFloor: true),
        new(CredibleThreatsOfViolence, "Credible threats of violence", IsHardFloor: true),
        new(IllegalContent, "Illegal content", IsHardFloor: true),
        new(CoercionAndExtortion, "Coercion and extortion", IsHardFloor: false),
        new(AdultContent, "Adult content", IsHardFloor: false),
        new(Harassment, "Harassment", IsHardFloor: false),
        new(Impersonation, "Impersonation", IsHardFloor: false),
        new(CredentialRequest, "Credential requests", IsHardFloor: false),
        new(PaymentRedirection, "Payment redirection", IsHardFloor: false),
        new(MalwareAndAttachments, "Malware and dangerous attachments", IsHardFloor: false),
        new(Promotion, "Promotion and marketing", IsHardFloor: false),
        new(UnsolicitedMail, "Unsolicited mail", IsHardFloor: false),
    ];

    /// <summary>The floor a hard-floor category may never sit below, and its minimum declared level.</summary>
    public const ToleranceLevel HardFloorMinimum = ToleranceLevel.Strict;

    private static readonly Dictionary<string, PolicyCategory> ById =
        All.ToDictionary(category => category.Id, StringComparer.Ordinal);

    public static bool TryGet(string? id, out PolicyCategory category)
    {
        if (id is null)
        {
            category = null!;
            return false;
        }

        return ById.TryGetValue(id, out category!);
    }

    public static PolicyCategory Get(string id) =>
        TryGet(id, out var category)
            ? category
            : throw new KeyNotFoundException($"Unknown policy category '{id}'.");

    public static bool IsHardFloor(string? id) => TryGet(id, out var category) && category.IsHardFloor;
}

/// <summary>The vertical a deployment serves. Unknown is a valid declaration with conservative defaults.</summary>
public enum PolicyVertical
{
    Unknown = 0,
    Commerce = 1,
    Dating = 2,
    Education = 3,
    Healthcare = 4,
    Enterprise = 5,
}

/// <summary>
/// Who the deployment's traffic is expected to reach.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is deliberately available and deliberately treated as the conservative
/// case: a deployment that cannot declare its audience does not get the adults defaults (spec 9.15).
/// </remarks>
public enum PolicyAudience
{
    Unknown = 0,
    Adults = 1,
    Minors = 2,
    Mixed = 3,
}

/// <summary>The declared context a pack's defaults come from (spec 9.15).</summary>
public sealed record PolicyPackContext
{
    public required PolicyVertical Vertical { get; init; }

    public required PolicyAudience Audience { get; init; }

    /// <summary>
    /// Country or jurisdiction code, e.g. <c>US</c>, <c>GB</c>, <c>EU</c>. Free of a fixed list
    /// because the legal surface is not the engine's to enumerate, but never blank: an undeclared
    /// jurisdiction silently selects no legal constraints.
    /// </summary>
    public required string Jurisdiction { get; init; }
}

/// <summary>What content, if any, a pack is allowed to retain.</summary>
public enum TextCaptureMode
{
    /// <summary>No content beyond the evidence rows, which never carry message text.</summary>
    None = 0,

    /// <summary>Envelope and header metadata only.</summary>
    MetadataOnly = 1,

    /// <summary>A bounded excerpt, for human review of an escalated case.</summary>
    Excerpt = 2,

    /// <summary>The full body. The widest PII surface, and the most tightly bound.</summary>
    Full = 3,
}

/// <summary>
/// The pack's content-capture rules. A binding, not this record, is what grants capture for a
/// scope: the pack states the widest mode the deployment may be bound to (spec 9.13, <c>PII</c> tier).
/// </summary>
public sealed record TextCaptureRules
{
    public required TextCaptureMode Mode { get; init; }

    /// <summary>Bound on a captured excerpt. Zero when nothing is captured.</summary>
    public required int MaxCharacters { get; init; }

    /// <summary>Retention clock for captured content, in days. Zero when nothing is captured.</summary>
    public required int RetentionDays { get; init; }

    /// <summary>What is stored and for how long, in operator-facing words. Required when capturing.</summary>
    public required string Statement { get; init; }

    public static TextCaptureRules Disabled { get; } = new()
    {
        Mode = TextCaptureMode.None,
        MaxCharacters = 0,
        RetentionDays = 0,
        Statement = "No message content is retained.",
    };
}

/// <summary>
/// A versioned policy pack: the declared context, the per-category tolerance, the action ceiling and
/// the provenance that makes a decision replayable.
/// </summary>
/// <remarks>
/// <para>
/// The contract is spec 9.15's list, and every field is present because a decision that cannot name
/// it cannot be replayed: id and version, context declaration, per-category level, action ceiling,
/// capture-text rules, retention, the rule and scorer versions it references, author, approver and
/// effective date.
/// </para>
/// <para>
/// A pack is immutable by construction. Changing a level means a new version, with the previous one
/// passed to the validator so a lowering can be checked against the level it lowered: a mutable pack
/// would make "who approved this" a question about history rather than about the record.
/// </para>
/// </remarks>
public sealed record PolicyPack
{
    /// <summary>The tenant this pack belongs to. Thresholds never cross tenants without validation.</summary>
    public required string TenantId { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required PolicyPackContext Context { get; init; }

    /// <summary>
    /// The tolerance for each declared category. Every hard floor must be present; a missing floor
    /// is not "use the default", it is a pack that has not declared what it protects.
    /// </summary>
    public required IReadOnlyDictionary<string, ToleranceLevel> CategoryLevels { get; init; }

    /// <summary>
    /// The most protective verdict an automatic rule may reach under this pack. Block is not
    /// representable: blocking a sender or domain is human authority (spec 9.13, tier 4).
    /// </summary>
    public required PolicyVerdict ActionCeiling { get; init; }

    public required TextCaptureRules CaptureText { get; init; }

    /// <summary>How long evidence rows, which never carry message content, are retained.</summary>
    public required int EvidenceRetentionDays { get; init; }

    /// <summary>The rule-set version this pack was written against. A mismatch is not replayable.</summary>
    public required string RuleSetVersion { get; init; }

    /// <summary>The scorer version the referenced rules were calibrated against.</summary>
    public required string ScorerVersion { get; init; }

    public required string Author { get; init; }

    /// <summary>The named human who approved this version. Promotion is never anonymous.</summary>
    public required string Approver { get; init; }

    /// <summary>
    /// Required when a category is lowered or the capture mode is widened. It must name a
    /// <em>different</em> human from <see cref="Approver"/>: a second approval is a second person,
    /// not the same person typing twice.
    /// </summary>
    public string? SecondApprover { get; init; }

    public required DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>When the pack must be looked at again. Optional, and never a substitute for review.</summary>
    public DateTimeOffset? ReviewDue { get; init; }

    /// <summary>The pack version this one supersedes, as <c>id@version</c>. Audit, not authority.</summary>
    public string? Supersedes { get; init; }
}

/// <summary>One reason a pack was refused, with a stable code for the console and the tests.</summary>
public sealed record PolicyPackViolation
{
    public required string Code { get; init; }

    public required string Field { get; init; }

    public required string Message { get; init; }
}

/// <summary>The validator's answer. Invalid packs are refused whole, never partially applied.</summary>
public sealed record PolicyPackValidation
{
    public required IReadOnlyList<PolicyPackViolation> Violations { get; init; }

    public bool IsValid => Violations.Count == 0;

    public static PolicyPackValidation Valid { get; } = new() { Violations = [] };
}

/// <summary>
/// Validates a pack against the catalogue and the hard floors before it can be activated.
/// </summary>
/// <remarks>
/// <para>
/// Validation accumulates every violation rather than throwing on the first one, because an
/// operator fixing a pack needs the whole list and a validator that stops early teaches them to
/// resubmit until it goes quiet. Nothing is clamped: an invalid pack is refused, since a silently
/// repaired floor is a floor nobody approved.
/// </para>
/// <para>
/// <b>The hard floors are enforced here and cannot be configured around.</b> A floor at
/// <see cref="ToleranceLevel.Off"/> or <see cref="ToleranceLevel.Observe"/> is refused, not raised
/// to Strict, and the only route below Strict is the code change and recorded operator decision the
/// spec requires (spec 9.15). <see cref="ToleranceLevel.ZeroTolerance"/> is accepted for a floor:
/// it is stricter, not looser.
/// </para>
/// </remarks>
public static class PolicyPackValidator
{
    public const string TenantMissing = "pack.tenant_missing";
    public const string IdMissing = "pack.id_missing";
    public const string VersionMissing = "pack.version_missing";
    public const string JurisdictionMissing = "pack.context.jurisdiction_missing";
    public const string CategoryUnknown = "pack.category_unknown";
    public const string CategoryLevelsEmpty = "pack.category_levels_empty";
    public const string HardFloorUndeclared = "pack.hard_floor_undeclared";
    public const string HardFloorBelowStrict = "pack.hard_floor_below_strict";
    public const string MixedAudienceGroomingNotStrict = "pack.mixed_audience_grooming_not_strict";
    public const string ActionCeilingInvalid = "pack.action_ceiling_invalid";
    public const string ActionCeilingBelowReview = "pack.action_ceiling_below_review";
    public const string CaptureStatementMissing = "pack.capture_text_statement_missing";
    public const string CaptureRetentionMissing = "pack.capture_text_retention_missing";
    public const string CaptureBoundMissing = "pack.capture_text_bound_missing";
    public const string CaptureFullRequiresSecondApprover = "pack.capture_text_full_requires_second_approver";
    public const string EvidenceRetentionMissing = "pack.evidence_retention_missing";
    public const string RuleSetVersionMissing = "pack.rule_set_version_missing";
    public const string ScorerVersionMissing = "pack.scorer_version_missing";
    public const string AuthorMissing = "pack.author_missing";
    public const string ApproverMissing = "pack.approver_missing";
    public const string EffectiveDateMissing = "pack.effective_date_missing";
    public const string ReviewDueBeforeEffective = "pack.review_due_before_effective";
    public const string PreviousPackMismatch = "pack.previous_pack_mismatch";
    public const string VersionNotAdvanced = "pack.version_not_advanced";
    public const string LevelLoweredWithoutSecondApprover = "pack.level_lowered_without_second_approver";
    public const string CaptureModeRaisedWithoutSecondApprover = "pack.capture_mode_raised_without_second_approver";
    public const string SecondApproverMustDiffer = "pack.second_approver_must_differ";

    /// <summary>
    /// Validates <paramref name="pack"/>, optionally against the pack version it supersedes.
    /// </summary>
    /// <param name="pack">The candidate pack.</param>
    /// <param name="previous">
    /// The pack this one replaces, when there is one. Supplying it is what makes "a lowering" a
    /// checkable fact rather than a claim: without it, every pack is a first pack and no level can
    /// be said to have moved.
    /// </param>
    /// <param name="validateApprover">
    /// False for a draft, which is authored before an approver exists. The approval step validates
    /// the same pack with the approver supplied, so a pack cannot reach the effective state without
    /// passing the full check. Defaults true so every existing call keeps its meaning.
    /// </param>
    public static PolicyPackValidation Validate(
        PolicyPack pack,
        PolicyPack? previous = null,
        bool validateApprover = true)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var violations = new List<PolicyPackViolation>();

        Require(pack.TenantId, TenantMissing, nameof(PolicyPack.TenantId), "A pack must belong to a tenant.", violations);
        Require(pack.Id, IdMissing, nameof(PolicyPack.Id), "A pack needs a stable id.", violations);
        Require(pack.Version, VersionMissing, nameof(PolicyPack.Version), "A pack needs a version.", violations);
        Require(pack.Context?.Jurisdiction, JurisdictionMissing, nameof(PolicyPack.Context), "A pack must declare its jurisdiction.", violations);
        Require(pack.RuleSetVersion, RuleSetVersionMissing, nameof(PolicyPack.RuleSetVersion), "A pack must reference the rule-set version it was written against.", violations);
        Require(pack.ScorerVersion, ScorerVersionMissing, nameof(PolicyPack.ScorerVersion), "A pack must reference the scorer version its thresholds were calibrated against.", violations);
        Require(pack.Author, AuthorMissing, nameof(PolicyPack.Author), "A pack must name its author.", violations);

        if (validateApprover)
        {
            Require(pack.Approver, ApproverMissing, nameof(PolicyPack.Approver), "A pack must name the human who approved it.", violations);
        }

        // A second approval is a second person, not the same person typing twice. The check is
        // outside the capture rule because it applies to every lowering, whether or not content is
        // captured, and it applies even when capture is disabled.
        if (!string.IsNullOrWhiteSpace(pack.SecondApprover)
            && string.Equals(pack.SecondApprover, pack.Approver, StringComparison.Ordinal))
        {
            violations.Add(Violation(
                SecondApproverMustDiffer,
                nameof(PolicyPack.SecondApprover),
                "The second approver must be a different human from the approver."));
        }

        if (pack.EffectiveFrom == default)
        {
            violations.Add(Violation(EffectiveDateMissing, nameof(PolicyPack.EffectiveFrom), "A pack must state when it takes effect."));
        }

        if (pack.ReviewDue is { } reviewDue && reviewDue <= pack.EffectiveFrom)
        {
            violations.Add(Violation(ReviewDueBeforeEffective, nameof(PolicyPack.ReviewDue), "A review date must fall after the effective date."));
        }

        ValidateCategories(pack, violations);
        ValidateCeiling(pack, violations);
        ValidateCapture(pack, violations);

        if (pack.EvidenceRetentionDays <= 0)
        {
            violations.Add(Violation(EvidenceRetentionMissing, nameof(PolicyPack.EvidenceRetentionDays), "Evidence retention must be a positive number of days."));
        }

        if (previous is not null)
        {
            ValidateAgainstPrevious(pack, previous, violations);
        }

        return new PolicyPackValidation { Violations = violations };
    }

    private static void ValidateCategories(PolicyPack pack, List<PolicyPackViolation> violations)
    {
        var levels = pack.CategoryLevels;
        if (levels is null || levels.Count == 0)
        {
            violations.Add(Violation(CategoryLevelsEmpty, nameof(PolicyPack.CategoryLevels), "A pack must declare at least the hard-floor categories."));
            return;
        }

        foreach (var (categoryId, level) in levels.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!PolicyCategories.TryGet(categoryId, out var category))
            {
                violations.Add(Violation(
                    CategoryUnknown,
                    $"{nameof(PolicyPack.CategoryLevels)}.{categoryId}",
                    $"'{categoryId}' is not a category the engine evaluates, so a tolerance for it would measure nothing."));
                continue;
            }

            if (!Enum.IsDefined(level))
            {
                violations.Add(Violation(
                    CategoryUnknown,
                    $"{nameof(PolicyPack.CategoryLevels)}.{categoryId}",
                    $"'{level}' is not a tolerance level."));
                continue;
            }

            if (category.IsHardFloor && level < PolicyCategories.HardFloorMinimum)
            {
                violations.Add(Violation(
                    HardFloorBelowStrict,
                    $"{nameof(PolicyPack.CategoryLevels)}.{categoryId}",
                    $"'{category.Label}' is a hard floor and cannot be {level}; the floor is {PolicyCategories.HardFloorMinimum}, and lowering it is a code change with a recorded operator decision."));
            }
        }

        foreach (var floor in PolicyCategories.All.Where(category => category.IsHardFloor))
        {
            if (!levels.ContainsKey(floor.Id))
            {
                violations.Add(Violation(
                    HardFloorUndeclared,
                    nameof(PolicyPack.CategoryLevels),
                    $"The hard floor '{floor.Label}' is not declared. A pack must state its floor; silence is not a default."));
            }
        }

        // A mixed or unknown audience runs grooming at Strict and escalates rather than guessing
        // which of its traffic is adult (spec 9.15). Declaring the category away is not a default.
        var conservativeAudience = pack.Context is null
            || pack.Context.Audience is PolicyAudience.Mixed or PolicyAudience.Unknown;

        if (conservativeAudience
            && levels.TryGetValue(PolicyCategories.ChildSafetyGrooming, out var grooming)
            && grooming < PolicyCategories.HardFloorMinimum)
        {
            violations.Add(Violation(
                MixedAudienceGroomingNotStrict,
                $"{nameof(PolicyPack.CategoryLevels)}.{PolicyCategories.ChildSafetyGrooming}",
                "A mixed or unknown audience runs child safety and grooming at Strict or above; it cannot be lowered to guess which traffic is adult."));
        }
    }

    private static void ValidateCeiling(PolicyPack pack, List<PolicyPackViolation> violations)
    {
        if (pack.ActionCeiling is PolicyVerdict.Permit)
        {
            violations.Add(Violation(
                ActionCeilingInvalid,
                nameof(PolicyPack.ActionCeiling),
                "An automatic ceiling of Permit is not a policy: a pack always carries hard floors at Strict or above, so its minimum automatic outcome is Review."));
            return;
        }

        // Block is not a PolicyVerdict, so it is structurally unreachable here: the ceiling can never
        // name an action the engine is not allowed to take automatically.

        var anyScoringCategory = pack.CategoryLevels is not null
            && pack.CategoryLevels.Values.Any(level => level >= ToleranceLevel.Standard);

        if (anyScoringCategory && pack.ActionCeiling < PolicyVerdict.Review)
        {
            violations.Add(Violation(
                ActionCeilingBelowReview,
                nameof(PolicyPack.ActionCeiling),
                $"A pack with a category at {ToleranceLevel.Standard} or above cannot cap automatic outcomes at {pack.ActionCeiling}; the ceiling would swallow the escalation the level promises."));
        }
    }

    private static void ValidateCapture(PolicyPack pack, List<PolicyPackViolation> violations)
    {
        var capture = pack.CaptureText;
        if (capture is null)
        {
            violations.Add(Violation(CaptureStatementMissing, nameof(PolicyPack.CaptureText), "A pack must state its capture-text rules; absence is not 'no capture'."));
            return;
        }

        if (capture.Mode == TextCaptureMode.None)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(capture.Statement))
        {
            violations.Add(Violation(
                CaptureStatementMissing,
                nameof(TextCaptureRules.Statement),
                "Capturing content requires a statement of what is stored and for how long."));
        }

        if (capture.RetentionDays <= 0)
        {
            violations.Add(Violation(
                CaptureRetentionMissing,
                nameof(TextCaptureRules.RetentionDays),
                "Capturing content requires a positive retention clock stated at capture time."));
        }

        if (capture.MaxCharacters <= 0)
        {
            violations.Add(Violation(
                CaptureBoundMissing,
                nameof(TextCaptureRules.MaxCharacters),
                "Capturing content requires a positive character bound; an unbounded excerpt is a full body."));
        }

        if (capture.Mode == TextCaptureMode.Full && string.IsNullOrWhiteSpace(pack.SecondApprover))
        {
            violations.Add(Violation(
                CaptureFullRequiresSecondApprover,
                nameof(PolicyPack.SecondApprover),
                "Full-body capture is the widest PII surface and requires a second approver."));
        }
    }

    private static void ValidateAgainstPrevious(
        PolicyPack pack,
        PolicyPack previous,
        List<PolicyPackViolation> violations)
    {
        if (!string.Equals(pack.TenantId, previous.TenantId, StringComparison.Ordinal)
            || !string.Equals(pack.Id, previous.Id, StringComparison.Ordinal))
        {
            violations.Add(Violation(
                PreviousPackMismatch,
                nameof(PolicyPack.Supersedes),
                "A pack may only be compared with the previous version of the same pack in the same tenant."));
            return;
        }

        if (string.Equals(pack.Version, previous.Version, StringComparison.Ordinal))
        {
            violations.Add(Violation(
                VersionNotAdvanced,
                nameof(PolicyPack.Version),
                "A change to a pack is a new version; a pack version is never edited in place."));
        }

        var hasSecondApprover = !string.IsNullOrWhiteSpace(pack.SecondApprover);
        var lowered = new List<string>();

        foreach (var (categoryId, level) in pack.CategoryLevels ?? new Dictionary<string, ToleranceLevel>())
        {
            if (previous.CategoryLevels.TryGetValue(categoryId, out var before) && level < before)
            {
                lowered.Add($"{categoryId} {before} -> {level}");
            }
        }

        if (lowered.Count > 0 && !hasSecondApprover)
        {
            violations.Add(Violation(
                LevelLoweredWithoutSecondApprover,
                nameof(PolicyPack.SecondApprover),
                "Lowering a category requires a second approver: " + string.Join(", ", lowered.Order(StringComparer.Ordinal)) + "."));
        }

        var captureRaised = pack.CaptureText is not null
            && previous.CaptureText is not null
            && pack.CaptureText.Mode > previous.CaptureText.Mode;

        if (captureRaised && !hasSecondApprover)
        {
            violations.Add(Violation(
                CaptureModeRaisedWithoutSecondApprover,
                nameof(PolicyPack.SecondApprover),
                $"Widening capture from {previous.CaptureText!.Mode} to {pack.CaptureText!.Mode} requires a second approver."));
        }
    }

    private static void Require(
        string? value,
        string code,
        string field,
        string message,
        List<PolicyPackViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(Violation(code, field, message));
        }
    }

    private static PolicyPackViolation Violation(string code, string field, string message) => new()
    {
        Code = code,
        Field = field,
        Message = message,
    };
}
