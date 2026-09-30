using StyloMail.Core;

namespace StyloMail.Policy;

/// <summary>
/// Turns evidence into an authorised action.
/// </summary>
/// <remarks>
/// <b>This is the only place an action is chosen.</b> Probabilistic components produce evidence;
/// policy authorises side effects. The precedence order below is the safety property, not a
/// stylistic choice, each tier can constrain the ones after it, and the lower tiers can never
/// relax the higher ones.
///
/// <orderedlist>
/// <item>Resource and authorisation controls (kill switch, hard quotas)</item>
/// <item>Verified security rule violations</item>
/// <item>Suspected-compromise posture</item>
/// <item>Behavioural and semantic risk</item>
/// <item>Recipient preference, lowest, and never able to override 1–3</item>
/// </orderedlist>
///
/// Within tier 4, a low index authorises delivery only when at least one available deterministic
/// signal was measured beside it. A semantic provider's false negative is indistinguishable from
/// its true negative, and it arrives as a low value rather than a missing one, so it raises the
/// covered fraction instead of lowering it and can satisfy both coverage guards. So the model's
/// calm is not acted on without something the pipeline can check: measured, and for the reason to
/// hold, agreeing. The two are not the same claim, and the difference is a filed gap rather than a
/// detail, since the deterministic findings are unweighted and cannot yet move the index.
/// </remarks>
public sealed class MailPolicyEngine
{
    /// <summary>
    /// Signals that indicate a security risk rather than a preference disagreement. Recipient
    /// preference may never relax a hold driven by one of these: "wanted promotion" is a
    /// legitimate preference, "wanted my bank details changed" is not.
    /// </summary>
    private static readonly string[] SecuritySignalIds =
    [
        "semantic.credential_request",
        "semantic.payment_redirection",
        "semantic.link_lure",
        "semantic.attachment_lure",
        "semantic.sensitive_data_request",
        "semantic.secrecy_bypass",
        "semantic.threat_reward_inducement",
        "semantic.identity_authority_claim",
    ];

    private readonly PolicyOptions _options;
    private readonly TimeProvider _time;

    public MailPolicyEngine(PolicyOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public PolicyDecision Decide(PolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Tier 1, resource and authorisation controls.
        // Deferral, not rejection: an exhausted quota or an engaged kill switch is an operational
        // state, not a verdict about this message. Rejecting permanently would turn our incident
        // into the sender's permanent mail loss.
        if (input.Context.EmergencyKillSwitchEngaged)
        {
            return Decision(
                MailAction.Defer,
                input,
                "policy.kill_switch",
                "Operator emergency kill switch is engaged; StyloMail is declining responsibility temporarily.",
                [],
                decidedBy: "resource-controls");
        }

        if (input.Context.OutboundQuotaExhausted && input.Direction == MailDirection.Outbound)
        {
            return Decision(
                MailAction.Defer,
                input,
                "policy.quota_exhausted",
                "Outbound recipient budget for this authenticated principal is exhausted; submission deferred.",
                [],
                decidedBy: "resource-controls");
        }

        // Tier 2, verified security rules. These do not consult the model and do not require its
        // confidence: a known hard violation is established, not inferred.
        if (input.Context.VerifiedSecurityRuleViolations.Count > 0)
        {
            return Decision(
                MailAction.Reject,
                input,
                "policy.verified_violation",
                $"Verified security rule violation: {string.Join(", ", input.Context.VerifiedSecurityRuleViolations)}.",
                input.Context.VerifiedSecurityRuleViolations,
                decidedBy: "verified-rules");
        }

        // Tier 3, suspected-compromise posture. An unresolved suspected outbound compromise stays
        // quarantined; it does not decay into delivery simply because nothing new arrived.
        if (input.Direction == MailDirection.Outbound
            && input.Context.BaselineFrozenForSuspectedCompromise)
        {
            return Decision(
                MailAction.Quarantine,
                input,
                "policy.suspected_compromise",
                "Outbound traffic from a principal whose trusted baseline is frozen pending compromise review.",
                ["profile.baseline_frozen"],
                decidedBy: "compromise-posture");
        }

        // Tier 4, behavioural and semantic risk.
        var decision = DecideByRisk(input);

        // Tier 5, recipient preference. Lowest precedence. It can relax a preference-shaped hold
        // and nothing else.
        if (decision.Action == MailAction.Hold
            && input.Context.RecipientPrefersThisTrafficClass
            && !HasElevatedSecuritySignal(input.Evidence))
        {
            return Decision(
                MailAction.Allow,
                input,
                "policy.recipient_preference",
                "Held traffic matched a traffic class this recipient has explicitly opted into, and no security signal is elevated.",
                decision.Reasons.SelectMany(r => r.EvidenceSignalIds).Distinct().ToList(),
                decidedBy: "recipient-preference");
        }

        return decision;
    }

    private PolicyDecision DecideByRisk(PolicyInput input)
    {
        var index = input.Risk.Index;
        var coverage = input.Risk.CoveredWeightFraction;
        var reasons = new List<ReasonCode>();

        var topSignals = input.Evidence
            .Where(e => e.Availability == EvidenceAvailability.Available && e.Value is > 0.5)
            .OrderByDescending(e => e.Value)
            .Select(e => e.SignalId)
            .ToList();

        if (input.Risk.Masked.Count > 0)
        {
            reasons.Add(new ReasonCode
            {
                Code = "evidence.masked_dimensions",
                Message =
                    $"{input.Risk.Masked.Count} dimension(s) contributed no evidence and were masked, "
                    + "not treated as zero.",
                EvidenceSignalIds = input.Risk.Masked.Select(m => m.SignalId).ToList(),
            });
        }

        // A low index driven by thin coverage is not a clean result. Refuse irreversible action.
        if (index >= _options.QuarantineThreshold)
        {
            if (coverage < _options.MinimumCoverageForIrreversibleAction)
            {
                reasons.Insert(0, new ReasonCode
                {
                    Code = "policy.insufficient_coverage_for_irreversible",
                    Message =
                        $"Risk index {index:0.00} exceeds the quarantine threshold, but only "
                        + $"{coverage:P0} of dimension weight was covered. Held for review rather than "
                        + "quarantined on incomplete evidence.",
                    EvidenceSignalIds = input.Risk.ContributingSignalIds,
                });

                return Hold(input, reasons);
            }

            reasons.Insert(0, new ReasonCode
            {
                Code = "policy.risk_above_quarantine",
                Message = $"Aggregate risk index {index:0.00} is at or above the quarantine threshold.",
                EvidenceSignalIds = topSignals,
            });

            return Decision(
                MailAction.Quarantine,
                input,
                reasons,
                decidedBy: "risk");
        }

        if (index >= _options.HoldThreshold)
        {
            // An allowlist entry relaxes a hold, but it is scoped and expiring and never reaches
            // above this tier.
            if (input.Context.AllowlistEntryValid)
            {
                return Decision(
                    MailAction.Allow,
                    input,
                    "policy.allowlist",
                    "Sender is covered by a currently valid allowlist entry; risk-based hold relaxed.",
                    topSignals,
                    decidedBy: "allowlist");
            }

            reasons.Insert(0, new ReasonCode
            {
                Code = "policy.risk_above_hold",
                Message = $"Aggregate risk index {index:0.00} is at or above the hold threshold.",
                EvidenceSignalIds = topSignals,
            });

            return Hold(input, reasons);
        }

        // Low risk is only reassuring if we actually looked. An index of 0.0 computed over a
        // coverage of 0.0 means nothing was measured, it is an outage, not a clean message, and
        // allowing it would make absence of evidence indistinguishable from evidence of safety.
        if (coverage < _options.MinimumCoverageForAllow)
        {
            reasons.Insert(0, new ReasonCode
            {
                Code = "policy.insufficient_coverage_to_allow",
                Message =
                    $"Risk index {index:0.00} is below the hold threshold, but only {coverage:P0} of "
                    + "dimension weight was covered. Too little evidence to allow and too little to "
                    + "reject; held for bounded re-evaluation.",
                EvidenceSignalIds = input.Risk.Masked.Select(m => m.SignalId).ToList(),
            });

            return Hold(input, reasons);
        }

        // A model's negative is indistinguishable from its silence: a false negative arrives as
        // Available with a value of 0.0, which raises the covered fraction and can satisfy both
        // guards above. So a low index is only authorising if something the pipeline can check was
        // measured at all. An allow resting on nothing but a probabilistic answer is a hold.
        //
        // What this does NOT do, and the two are not the same claim: it requires that checkable
        // evidence was measured, not that the checkable evidence agrees. Rows outside
        // DimensionWeights (every deterministic finding, since those weights are semantic-only) do
        // not contribute to the index, so a displayed-link mismatch at 1.0 cannot raise it and a
        // benign row beside it satisfies this gate. Filed high as
        // `unweighted-deterministic-findings-cannot-block-a`, and the repair is weights with
        // declared units rather than a threshold test here, because the deterministic signals carry
        // counts and ratios and bytes, and a bare "value above the hold threshold" rule would hold
        // every message with a stored attachment. Pinned by a test in the policy suite.
        var corroborating = input.Evidence
            .Where(e => e.Availability == EvidenceAvailability.Available
                && e.Origin == EvidenceOrigin.Deterministic)
            .Select(e => e.SignalId)
            .ToList();

        if (corroborating.Count == 0)
        {
            var uncheckable = input.Evidence
                .Where(e => e.Availability == EvidenceAvailability.Available
                    && e.Origin != EvidenceOrigin.Deterministic)
                .Select(e => string.IsNullOrWhiteSpace(e.SourceVersion) ? e.Origin.ToString() : e.SourceVersion)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            reasons.Insert(0, new ReasonCode
            {
                Code = "policy.allow_uncorroborated_by_deterministic_evidence",
                Message =
                    $"Risk index {index:0.00} is below the hold threshold, but no available "
                    + "deterministic signal corroborates it. "
                    + (uncheckable.Count > 0
                        ? $"The available evidence came from {string.Join(", ", uncheckable)}, and a "
                        : "No evidence was available at all, and no ")
                    + "probabilistic negative may authorise delivery on its own.",
                EvidenceSignalIds = input.Risk.ContributingSignalIds,
            });

            return Hold(input, reasons);
        }

        reasons.Insert(0, new ReasonCode
        {
            Code = "policy.risk_below_threshold",
            Message = $"Aggregate risk index {index:0.00} is below the hold threshold.",
            EvidenceSignalIds = input.Risk.ContributingSignalIds,
        });

        return Decision(MailAction.Allow, input, reasons, decidedBy: "risk");
    }

    /// <summary>
    /// Bounded hold. The deadline is clamped to the absolute ceiling so a hold can never be
    /// extended indefinitely by configuration drift.
    /// </summary>
    private PolicyDecision Hold(PolicyInput input, List<ReasonCode> reasons)
    {
        var now = _time.GetUtcNow();
        var deadline = now + _options.HoldWindow;
        var ceiling = now + _options.MaxHoldDeadline;

        if (deadline > ceiling)
        {
            deadline = ceiling;
        }

        var decision = Decision(MailAction.Hold, input, reasons, decidedBy: "risk");
        return decision with { ReEvaluateBy = deadline };
    }

    /// <summary>
    /// True when a security-class signal is present <em>and</em> elevated. Presence alone is not
    /// enough: a credential-request probability of 0.05 is not a reason to keep holding a message
    /// the recipient has opted into.
    /// </summary>
    private bool HasElevatedSecuritySignal(IReadOnlyList<Evidence> evidence)
        => evidence.Any(e =>
            SecuritySignalIds.Contains(e.SignalId, StringComparer.Ordinal)
            && e.Availability == EvidenceAvailability.Available
            && e.Value is { } value
            && value >= _options.HoldThreshold);

    private static PolicyDecision Decision(
        MailAction action,
        PolicyInput input,
        string code,
        string message,
        IReadOnlyList<string> signalIds,
        string decidedBy)
        => Decision(
            action,
            input,
            [new ReasonCode { Code = code, Message = message, EvidenceSignalIds = signalIds }],
            decidedBy);

    private static PolicyDecision Decision(
        MailAction action,
        PolicyInput input,
        List<ReasonCode> reasons,
        string decidedBy)
        => new()
        {
            Action = action,
            RiskIndex = input.Risk.Index,
            Reasons = reasons,
            ReEvaluateBy = null,
            DecidedBy = decidedBy,
        };
}
