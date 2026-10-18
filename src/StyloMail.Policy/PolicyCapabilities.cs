namespace StyloMail.Policy;

/// <summary>Every action the operator surface may be offered or asked about.</summary>
public enum PolicyActionKind
{
    Hold = 0,
    Quarantine = 1,
    Review = 2,
    ReleaseQuarantine = 3,
    PauseSender = 4,
    ResumeSender = 5,
    BlockSender = 6,
    UnblockSender = 7,
    MonitorAllMail = 8,
    CaptureText = 9,
    ReleaseReview = 10,
    QuarantineReview = 11,
    RejectReview = 12,
}

/// <summary>Whether an action can be offered at all right now.</summary>
public enum PolicyCapabilityAvailability
{
    Available = 0,
    NotAvailable = 1,
}

/// <summary>What the action does to delivery, stated explicitly rather than inferred from a name.</summary>
public enum PolicyResultingState
{
    Delivered = 0,
    Delayed = 1,
    WithheldForReview = 2,
    Contained = 3,
    Rejected = 4,
    Deferred = 5,
    NotApplicable = 6,
}

/// <summary>
/// Whether a consequence input was measured, was not produced, or does not apply.
/// </summary>
/// <remarks>
/// <c>NotProduced</c> and <c>NotApplicable</c> are distinct because a failed query and a question
/// that does not apply are different facts, and neither is zero. The console renders both as an
/// explicit absence rather than a number.
/// </remarks>
public enum ConsequenceAvailability
{
    Measured = 0,
    NotProduced = 1,
    NotApplicable = 2,
}

/// <summary>One named fact a consequence template may use.</summary>
public sealed record ConsequenceInputDescriptor
{
    public required string Name { get; init; }

    public required string Unit { get; init; }

    public required ConsequenceAvailability DefaultAvailability { get; init; }

    /// <summary>Where the value would come from, e.g. <c>decision_ledger</c> or <c>pack</c>.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// One action's physical capability and the facts its consequence panel is built from.
/// </summary>
/// <remarks>
/// <para>
/// This is a descriptor, not a decision. It says what the action does, what privilege it needs, how
/// it can be reversed and which inputs its copy uses. The decision to apply one is policy's, and
/// the sentence is the console's.
/// </para>
/// <para>
/// <see cref="PolicyActionKind.Review"/> is present but not available in this phase: it is a real
/// requested action with no state yet, and the honest descriptor says so rather than hiding it.
/// </para>
/// </remarks>
/// <summary>The review terms a Review capability carries, and the console renders.</summary>
public sealed record PolicyReviewTerms
{
    /// <summary>Disclose-safe queue name, or <c>unknown</c> when it cannot be disclosed.</summary>
    public required string Queue { get; init; }

    public string? Owner { get; init; }

    public required TimeSpan Deadline { get; init; }

    /// <summary>What happens at the deadline. Quarantine in v1; never a release.</summary>
    public required string EscalationAction { get; init; }

    /// <summary>The actions available to the reviewer.</summary>
    public required string NextAction { get; init; }
}

public sealed record PolicyActionCapability
{
    public required PolicyActionKind Kind { get; init; }

    public required PolicyCapabilityAvailability Availability { get; init; }

    /// <summary>Stable refusal code when the action cannot be offered. Never prose.</summary>
    public string? DenialReason { get; init; }

    /// <summary>Tier on the authority ladder (spec 9.13). PII capture is marked separately.</summary>
    public required int AuthorityTier { get; init; }

    /// <summary>True for the capture-text path, whose authority is a binding rather than a tier.</summary>
    public bool Pii { get; init; }

    public required bool Automatic { get; init; }

    public required bool RequiresHumanApproval { get; init; }

    public required string RequiredPrivilege { get; init; }

    public required IReadOnlyList<BindingScope> Scopes { get; init; }

    public required PolicyResultingState ResultingState { get; init; }

    public required bool Reversible { get; init; }

    public PolicyActionKind? ReversalAction { get; init; }

    public TimeSpan? MaximumDuration { get; init; }

    public required bool ReasonRequired { get; init; }

    public required string AuditEventKind { get; init; }

    /// <summary>e.g. <c>none</c>, <c>review_queue</c>, <c>sender_owner</c>, <c>tenant_administrators</c>.</summary>
    public required string NotificationEffect { get; init; }

    /// <summary>Review terms, present only for the Review action.</summary>
    public PolicyReviewTerms? Review { get; init; }

    public required string ConsequenceTemplateId { get; init; }

    public required int ConsequenceTemplateVersion { get; init; }

    public required IReadOnlyList<ConsequenceInputDescriptor> Inputs { get; init; }
}

/// <summary>
/// The static capability catalogue. No pack, tenant or decision is consulted, so it is the same
/// answer for every caller and cannot become a decision by accident.
/// </summary>
public static class PolicyCapabilityCatalog
{
    private static readonly BindingScope[] MessageScopes =
        [BindingScope.Conversation, BindingScope.Entity, BindingScope.Tenant];

    private static readonly BindingScope[] SenderScopes =
        [BindingScope.Entity, BindingScope.Domain, BindingScope.Company, BindingScope.Tenant];

    private static readonly BindingScope[] TenantScopes = [BindingScope.Tenant];

    private static readonly BindingScope[] TextScopes =
        [BindingScope.Conversation, BindingScope.Entity, BindingScope.Domain, BindingScope.Company, BindingScope.Tenant];

    private static readonly IReadOnlyList<ConsequenceInputDescriptor> CommonInputs =
    [
        Input("effective_level", "level", ConsequenceAvailability.NotProduced, "pack"),
        Input("hard_floor", "boolean", ConsequenceAvailability.NotProduced, "pack"),
        Input("hard_floor_reason", "text", ConsequenceAvailability.NotProduced, "pack"),
        Input("target_scope_kind", "scope", ConsequenceAvailability.NotProduced, "binding"),
        Input("target_display_reference", "text", ConsequenceAvailability.NotProduced, "binding"),
        Input("affected_messages", "messages", ConsequenceAvailability.NotProduced, "decision_ledger"),
        Input("affected_relationships", "relationships", ConsequenceAvailability.NotProduced, "decision_ledger"),
        Input("pack_version", "version", ConsequenceAvailability.NotProduced, "pack"),
        Input("rule_set_version", "version", ConsequenceAvailability.NotProduced, "pack"),
        Input("evidence_digest", "digest", ConsequenceAvailability.NotProduced, "decision_ledger"),
    ];

    private static readonly IReadOnlyList<ConsequenceInputDescriptor> ReviewInputs =
    [
        Input("review_queue", "text", ConsequenceAvailability.NotApplicable, "review_queue"),
        Input("review_owner", "text", ConsequenceAvailability.NotApplicable, "review_queue"),
        Input("review_deadline", "timestamp", ConsequenceAvailability.NotApplicable, "pack"),
        Input("review_escalation", "action", ConsequenceAvailability.NotApplicable, "pack"),
        Input("review_next_action", "action", ConsequenceAvailability.NotApplicable, "review_queue"),
    ];

    private static readonly IReadOnlyList<ConsequenceInputDescriptor> BindingInputs =
    [
        Input("binding_scope", "scope", ConsequenceAvailability.NotApplicable, "binding"),
        Input("binding_expires_at", "timestamp", ConsequenceAvailability.NotApplicable, "binding"),
        Input("binding_review_by", "timestamp", ConsequenceAvailability.NotApplicable, "binding"),
        Input("binding_reversible", "boolean", ConsequenceAvailability.NotApplicable, "binding"),
    ];

    private static readonly IReadOnlyList<ConsequenceInputDescriptor> CaptureInputs =
    [
        Input("capture_retention_days", "days", ConsequenceAvailability.NotApplicable, "pack"),
        Input("capture_max_characters", "characters", ConsequenceAvailability.NotApplicable, "pack"),
        Input("capture_statement", "text", ConsequenceAvailability.NotApplicable, "pack"),
    ];

    private static readonly IReadOnlyList<ConsequenceInputDescriptor> EffectInputs =
    [
        Input("reversal_action", "action", ConsequenceAvailability.NotProduced, "capability"),
        Input("reversal_deadline", "timestamp", ConsequenceAvailability.NotProduced, "capability"),
        Input("notification_effect", "text", ConsequenceAvailability.NotProduced, "capability"),
        Input("audit_event_kind", "text", ConsequenceAvailability.NotProduced, "capability"),
    ];

    /// <summary>Every descriptor, in a stable order. Never filtered by tenant or pack.</summary>
    public static IReadOnlyList<PolicyActionCapability> All { get; } =
    [
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.Hold,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 2,
            Automatic = true,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Delayed,
            Reversible = true,
            MaximumDuration = TimeSpan.FromSeconds(30),
            ReasonRequired = false,
            AuditEventKind = "decision.hold",
            NotificationEffect = "sender_owner",
            ConsequenceTemplateId = "policy.hold",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. BindingInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.Quarantine,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 3,
            Automatic = true,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Contained,
            Reversible = true,
            ReversalAction = PolicyActionKind.ReleaseQuarantine,
            ReasonRequired = false,
            AuditEventKind = "decision.quarantine",
            NotificationEffect = "review_queue",
            ConsequenceTemplateId = "policy.quarantine",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. BindingInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.Review,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 2,
            Automatic = true,
            RequiresHumanApproval = true,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.WithheldForReview,
            Reversible = true,
            ReasonRequired = false,
            AuditEventKind = "decision.review",
            NotificationEffect = "review_queue",
            Review = new PolicyReviewTerms
            {
                Queue = "review",
                Owner = null,
                Deadline = TimeSpan.FromHours(24),
                EscalationAction = "quarantine",
                NextAction = "release|quarantine|reject",
            },
            ConsequenceTemplateId = "policy.review",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. ReviewInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.ReleaseQuarantine,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 2,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Delivered,
            Reversible = false,
            ReasonRequired = true,
            AuditEventKind = "quarantine.release",
            NotificationEffect = "none",
            ConsequenceTemplateId = "policy.release_quarantine",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.PauseSender,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 3,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Administer",
            Scopes = SenderScopes,
            ResultingState = PolicyResultingState.Deferred,
            Reversible = true,
            ReversalAction = PolicyActionKind.ResumeSender,
            ReasonRequired = true,
            AuditEventKind = "sender.pause",
            NotificationEffect = "sender_owner",
            ConsequenceTemplateId = "policy.pause_sender",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. BindingInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.ResumeSender,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 3,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Administer",
            Scopes = SenderScopes,
            ResultingState = PolicyResultingState.Delivered,
            Reversible = true,
            ReversalAction = PolicyActionKind.PauseSender,
            ReasonRequired = true,
            AuditEventKind = "sender.resume",
            NotificationEffect = "sender_owner",
            ConsequenceTemplateId = "policy.resume_sender",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.BlockSender,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 4,
            Automatic = false,
            RequiresHumanApproval = true,
            RequiredPrivilege = "Administer",
            Scopes = SenderScopes,
            ResultingState = PolicyResultingState.Rejected,
            Reversible = true,
            ReversalAction = PolicyActionKind.UnblockSender,
            ReasonRequired = true,
            AuditEventKind = "sender.block",
            NotificationEffect = "tenant_administrators",
            ConsequenceTemplateId = "policy.block_sender",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. BindingInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.UnblockSender,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 4,
            Automatic = false,
            RequiresHumanApproval = true,
            RequiredPrivilege = "Administer",
            Scopes = SenderScopes,
            ResultingState = PolicyResultingState.Delivered,
            Reversible = true,
            ReversalAction = PolicyActionKind.BlockSender,
            ReasonRequired = true,
            AuditEventKind = "sender.unblock",
            NotificationEffect = "tenant_administrators",
            ConsequenceTemplateId = "policy.unblock_sender",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.MonitorAllMail,
            Availability = PolicyCapabilityAvailability.NotAvailable,
            DenialReason = "monitor_all_mail_not_implemented",
            AuthorityTier = 1,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Administer",
            Scopes = TenantScopes,
            ResultingState = PolicyResultingState.NotApplicable,
            Reversible = true,
            ReasonRequired = true,
            AuditEventKind = "monitor.all_mail",
            NotificationEffect = "tenant_administrators",
            ConsequenceTemplateId = "policy.monitor_all_mail",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.ReleaseReview,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 2,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Delivered,
            Reversible = false,
            ReasonRequired = true,
            AuditEventKind = "review.release",
            NotificationEffect = "none",
            ConsequenceTemplateId = "policy.release_review",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. ReviewInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.QuarantineReview,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 3,
            Automatic = false,
            RequiresHumanApproval = false,
            RequiredPrivilege = "Review",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Contained,
            Reversible = true,
            ReversalAction = PolicyActionKind.ReleaseQuarantine,
            ReasonRequired = true,
            AuditEventKind = "review.quarantine",
            NotificationEffect = "review_queue",
            ConsequenceTemplateId = "policy.quarantine_review",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. ReviewInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.RejectReview,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 4,
            Automatic = false,
            RequiresHumanApproval = true,
            RequiredPrivilege = "Administer",
            Scopes = MessageScopes,
            ResultingState = PolicyResultingState.Rejected,
            Reversible = false,
            ReasonRequired = true,
            AuditEventKind = "review.reject",
            NotificationEffect = "tenant_administrators",
            ConsequenceTemplateId = "policy.reject_review",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. ReviewInputs, .. EffectInputs],
        },
        new PolicyActionCapability
        {
            Kind = PolicyActionKind.CaptureText,
            Availability = PolicyCapabilityAvailability.Available,
            AuthorityTier = 4,
            Pii = true,
            Automatic = false,
            RequiresHumanApproval = true,
            RequiredPrivilege = "Administer",
            Scopes = TextScopes,
            ResultingState = PolicyResultingState.NotApplicable,
            Reversible = true,
            ReasonRequired = true,
            AuditEventKind = "capture.text",
            NotificationEffect = "tenant_administrators",
            ConsequenceTemplateId = "policy.capture_text",
            ConsequenceTemplateVersion = 1,
            Inputs = [.. CommonInputs, .. CaptureInputs, .. BindingInputs, .. EffectInputs],
        },
    ];

    private static ConsequenceInputDescriptor Input(
        string name,
        string unit,
        ConsequenceAvailability availability,
        string source) => new()
        {
            Name = name,
            Unit = unit,
            DefaultAvailability = availability,
            Source = source,
        };
}
