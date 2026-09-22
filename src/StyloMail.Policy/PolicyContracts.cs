using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>
/// The facts policy needs beyond the message itself. Supplied by the caller, never inferred
/// from message content.
/// </summary>
public sealed record PolicyContext
{
    /// <summary>
    /// Operator kill switch. Whatever else is true, this constrains the outcome — an allowlist
    /// entry does not bypass an emergency ceiling.
    /// </summary>
    public required bool EmergencyKillSwitchEngaged { get; init; }

    /// <summary>True when the authenticated principal has exhausted its outbound recipient budget.</summary>
    public required bool OutboundQuotaExhausted { get; init; }

    /// <summary>
    /// Violations established by explicit, verified security rules — not model judgements.
    /// These do not require model confidence to act on.
    /// </summary>
    public required IReadOnlyList<string> VerifiedSecurityRuleViolations { get; init; }

    /// <summary>True when a currently valid allowlist entry covers this sender.</summary>
    public required bool AllowlistEntryValid { get; init; }

    /// <summary>True when the recipient has expressed a scoped preference (e.g. "wanted promotion").</summary>
    public bool RecipientPrefersThisTrafficClass { get; init; }

    /// <summary>True when the sender's baseline is frozen because compromise is suspected.</summary>
    public required bool BaselineFrozenForSuspectedCompromise { get; init; }
}

/// <summary>The action policy authorises, with the reasoning that justifies it.</summary>
public sealed record PolicyDecision
{
    public required MailAction Action { get; init; }

    /// <summary>The aggregate risk index that informed the decision. An index, not a probability.</summary>
    public required double RiskIndex { get; init; }

    /// <summary>Ordered most-significant first, each grounded in specific signals.</summary>
    public required IReadOnlyList<ReasonCode> Reasons { get; init; }

    /// <summary>For holds: when the message must be re-evaluated. A hold never extends silently forever.</summary>
    public DateTimeOffset? ReEvaluateBy { get; init; }

    /// <summary>Precedence tier that produced the decision, for audit and for explaining overrides.</summary>
    public required string DecidedBy { get; init; }
}

/// <summary>Inputs the policy engine evaluates, in precedence order.</summary>
public sealed record PolicyInput
{
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required RiskIndexResult Risk { get; init; }

    public required PolicyContext Context { get; init; }

    public required MailDirection Direction { get; init; }
}

/// <summary>
/// Thresholds and weights. Versioned: a change here invalidates prior decisions for replay purposes.
/// </summary>
public sealed class PolicyOptions
{
    /// <summary>Version stamped on every decision. Bump whenever any value below changes.</summary>
    public string Version { get; set; } = "policy/1";

    /// <summary>At or above this index, the message is held rather than allowed.</summary>
    public double HoldThreshold { get; set; } = 0.55;

    /// <summary>At or above this index, the message is quarantined.</summary>
    public double QuarantineThreshold { get; set; } = 0.80;

    /// <summary>
    /// Below this covered weight fraction, no irreversible action may be taken on risk alone.
    /// Thin evidence yields a bounded hold, not a rejection.
    /// </summary>
    public double MinimumCoverageForIrreversibleAction { get; set; } = 0.60;

    /// <summary>
    /// Below this covered weight fraction, a message may <b>not</b> be allowed on risk alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the other half of <see cref="MinimumCoverageForIrreversibleAction"/>, and without it
    /// the engine fails open. A total semantic outage produces an index of 0.0 over a coverage of
    /// 0.0 — which is under every threshold, so a threshold-only decision returns Allow. Absence of
    /// evidence would then be indistinguishable from evidence of safety, and the one failure the
    /// system must never have is silently blessing everything it could not see.
    /// </para>
    /// <para>
    /// The two floors together bracket the honest answer: too little evidence to allow, too little
    /// to reject, so the middle is a bounded hold.
    /// </para>
    /// <para>
    /// Set this to 0 for a deployment that genuinely runs without semantic evidence by design (the
    /// source spec's local-evidence-only tenant). That is a deliberate configuration stating "I
    /// expect no semantic coverage", not an outage being tolerated by accident.
    /// </para>
    /// </remarks>
    public double MinimumCoverageForAllow { get; set; } = 0.30;

    /// <summary>Default hold window when a message is held.</summary>
    public TimeSpan HoldWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Absolute ceiling on any hold. Re-evaluation must happen by then.</summary>
    public TimeSpan MaxHoldDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Weights per semantic signal id for the composite index. Correlated dimensions are summed
    /// with weights — never multiplied as though independent.
    /// </summary>
    public Dictionary<string, double> DimensionWeights { get; set; } = new(StringComparer.Ordinal)
    {
        ["semantic.credential_request"] = 1.0,
        ["semantic.payment_redirection"] = 1.0,
        ["semantic.link_lure"] = 0.8,
        ["semantic.attachment_lure"] = 0.8,
        ["semantic.sensitive_data_request"] = 0.8,
        ["semantic.secrecy_bypass"] = 0.7,
        ["semantic.threat_reward_inducement"] = 0.7,
        ["semantic.identity_authority_claim"] = 0.6,
        ["semantic.urgency_pressure"] = 0.4,
        ["semantic.unsolicited_solicitation"] = 0.3,
        ["semantic.transactional_character"] = 0.2,
        [SemanticDimensions.ConversationalContinuityId] = 0.5,
    };
}
