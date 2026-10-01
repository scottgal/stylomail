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
/// <item>Verified security rule violations, and the checkable facts that must refuse: a violation
/// rejects, a refusing finding holds</item>
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
/// hold, agreeing.
///
/// <para>
/// The gate is kept beside the coverage floor rather than folded into it (decision 42). The floor
/// answers "did we answer the questions we could ask"; it cannot answer "did a non-probabilistic
/// check agree". A message whose deterministic questions are all inapplicable (plain text, no links,
/// no attachments) has them removed from the denominator, so its coverage can be high on semantic
/// answers alone, and without this gate it would allow on the model's word with no deterministic row
/// measured at all, which is the hazard this tier exists to prevent.
/// </para>
///
/// <para>
/// The risk-shaped deterministic findings are now weighted and do move the index (decision 42), so
/// the gate's "was something checkable measured" check is no longer the only route by which a
/// deterministic fact can matter: a finding that objects contributes to the index beside it.
/// </para>
///
/// <para>
/// <b>A low index also requires the semantic layer to have answered, or never to have been asked.</b>
/// Weighting the structural findings moved coverage in a direction that lets structure alone clear the
/// allow floor, so the tier refuses a delivery while a semantic question was asked and left
/// unanswered. This is the same asymmetry the gate above rests on: a hold queues a message for
/// review and an allow delivers it, and the structural layer is the one an adversary satisfies by
/// construction. The gate is absolute: it is not exempted by the allow floor, because a deployment
/// whose floor is zero is the one configuration in which a full blackout would otherwise allow on
/// local evidence alone.
/// </para>
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

        // Tier 2, continued. A checkable fact that must refuse, but as a Hold rather than a Reject:
        // each is established rather than inferred, like a violation above, yet each has a
        // legitimate-traffic population that a permanent verdict would destroy and a review can
        // release (the membership and the mail each would hold are declared in
        // DeterministicFindings.Refusing, in Policy rather than with the caller, because which facts
        // can refuse an action is itself an action-shaped judgement).
        //
        // The rule reads the evidence, so it needs nobody to populate a second field and it reads the
        // three states the wire already carries: Available with a normalised value of 1.0 is a finding
        // that is PRESENT and refuses; Available with 0.0 is measured and clean; NotApplicable (never
        // in question) and Unavailable (in question, unanswered) refuse nothing, because neither is a
        // fact about the message. That is also why no evaluability flag is needed here: an unattempted
        // question is simply a row that does not establish anything, not a clean bill of health.
        //
        // Placed in tier 2 rather than in the risk path on purpose: tier 5 (recipient preference) can
        // relax a preference-shaped hold, and it must never relax this one, so the decision is taken
        // above it and returns before tier 5 runs. It does not feed the index either: the fact is a
        // refusal, and the index's job is to grade, not to refuse.
        var refusing = input.Evidence
            .Where(e => e.Availability == EvidenceAvailability.Available
                && e.Value is { } value
                && DeterministicFindings.Establishes(e.SignalId, value))
            .Select(e => e.SignalId)
            .ToList();

        if (refusing.Count > 0)
        {
            return Hold(
                input,
                [
                    new ReasonCode
                    {
                        Code = "policy.refusing_finding",
                        Message =
                            "A checkable fact that must refuse delivery was established: "
                            + $"{string.Join(", ", refusing)}. Held for review rather than rejected, "
                            + "because this signal has legitimate-traffic populations that a permanent "
                            + "verdict would destroy.",
                        EvidenceSignalIds = refusing,
                    },
                ],
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
            return NoteUnappliedRefusalChecks(
                input,
                Decision(
                    MailAction.Allow,
                    input,
                    "policy.recipient_preference",
                    "Held traffic matched a traffic class this recipient has explicitly opted into, and no security signal is elevated.",
                    decision.Reasons.SelectMany(r => r.EvidenceSignalIds).Distinct().ToList(),
                    decidedBy: "recipient-preference"));
        }

        // Every allow leaves through here or the tier-5 branch above, so the not-evaluated note is
        // attached in one place rather than remembered at each allow site.
        return decision.Action == MailAction.Allow
            ? NoteUnappliedRefusalChecks(input, decision)
            : decision;
    }

    /// <summary>
    /// A delivery whose refusal check did not run says so, rather than reading as checked and clean.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trigger is the evidence, not a caller-supplied list: it fires when a refusing id has no
    /// <c>Available</c> row and the question was actually put, which is the absent case and the
    /// <c>Unavailable</c> case. A <c>NotApplicable</c> row is not a trigger, because a question the
    /// message never raised is not a check that failed to run, and reading a deployment's shape as an
    /// outage is the mistake this deliberately avoids.
    /// </para>
    /// <para>
    /// <b>Why the NotApplicable carve-out is stated rather than inferred from "no Available row".</b>
    /// The requirement that produced this note carried both phrasings and they disagree at the edge: "a
    /// refusal-shaped id has no Available row" read literally would include a <c>NotApplicable</c> row,
    /// while "in question and unanswered" excludes it. The intent clause governs, and it is corroborated
    /// twice: a <c>NotApplicable</c>-only list is defined elsewhere as "not an outage, nothing was asked
    /// of the provider, so nothing was lost", and treating it as an outage would make an
    /// inapplicable-only deployment defer rather than deliver. Recorded here because the literal clause
    /// is the one a later reader is likely to re-derive from.
    /// </para>
    /// <para>
    /// Appended, never prepended, so the reason that produced the decision is still read first. This is
    /// the one thing carried over from the deleted <c>PolicyContext.RefusingFindings</c> field, and
    /// re-keyed to evidence: under the field every allow carried the note because nothing populated it,
    /// whereas here it appears only where a refusal check really was in question and unattempted.
    /// </para>
    /// </remarks>
    private static PolicyDecision NoteUnappliedRefusalChecks(PolicyInput input, PolicyDecision decision)
    {
        var unevaluated = DeterministicFindings.Refusing
            .Where(id => HasNoMeasuredRow(input.Evidence, id))
            .ToList();

        if (unevaluated.Count == 0)
        {
            return decision;
        }

        var reasons = decision.Reasons.ToList();
        reasons.Add(new ReasonCode
        {
            Code = "policy.refusing_findings_not_evaluated",
            Message =
                "A fact that must refuse delivery was not evaluated: "
                + $"{string.Join(", ", unevaluated)}. "
                + "A delivery is not a clean bill of health for a check that did not run.",
            EvidenceSignalIds = unevaluated,
        });

        return decision with { Reasons = reasons };
    }

    /// <summary>
    /// True when the refusing id was put to the message and no row answered it: either no row exists at
    /// all, or one exists and reports <c>Unavailable</c>. A measured row answers it whichever way it
    /// went, and a <c>NotApplicable</c> row was never asked.
    /// </summary>
    private static bool HasNoMeasuredRow(IReadOnlyList<Evidence> evidence, string signalId)
    {
        var rows = evidence.Where(e => e.SignalId == signalId).ToList();

        if (rows.Any(r => r.Availability == EvidenceAvailability.Available))
        {
            return false;
        }

        return rows.Count == 0
            || rows.Any(r => r.Availability == EvidenceAvailability.Unavailable);
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
            // A masked row is out of the arithmetic, and the reasons for that are not one: a row
            // nothing was measured for, and a row excluded from the index by policy whichever way it
            // answered (decision 32). The two are said plainly and separately rather than wrapped in
            // one sentence: "not treated as zero" is true of the first and false of the second, and
            // the top line is what a reader takes away even when a per-row reason disambiguates it.
            var unmeasured = input.Risk.Masked.Count(m => m.Reason is null);
            var excluded = input.Risk.Masked.Where(m => m.Reason is not null).ToList();

            var clauses = new List<string>();
            if (unmeasured > 0)
            {
                clauses.Add(
                    $"{unmeasured} dimension(s) were not measured and are masked rather than counted "
                    + "as zero: an absent answer is not a calm one.");
            }

            if (excluded.Count > 0)
            {
                clauses.Add(
                    $"{excluded.Count} dimension(s) were excluded by policy and leave the index and "
                    + "its denominator deliberately.");
            }

            reasons.Add(new ReasonCode
            {
                Code = "evidence.masked_dimensions",
                Message =
                    string.Join(" ", clauses)
                    + (excluded.Count == 0
                        ? string.Empty
                        : " " + string.Join("; ", excluded.Select(m => $"{m.SignalId}: {m.Reason}"))),
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
        // evidence was measured, not that the checkable evidence agrees. The two are now separate
        // arms of one rule rather than two claims about different mechanisms, because the
        // deterministic findings carry weight (decision 42): an objecting finding moves the index
        // above on its own account, and this gate declines to act on a low index until something
        // checkable was measured at all. The gate is kept rather than folded into the coverage
        // floor, because the floor can be satisfied by a feature-poor message's semantic rows alone
        // (its inapplicable deterministic questions have left the denominator), so the floor would
        // let a plain-text message allow on the model's word with no deterministic row measured.
        // Belt and braces, as ruled. Pinned by a test in the policy suite.
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
                    $"Risk index {index:0.00} is below the hold threshold, but no deterministic "
                    + "signal was measured to corroborate it. "
                    // Both branches have to end in the same prohibition. The branch above used to
                    // read "and a probabilistic negative may authorise delivery on its own", which
                    // told the operator the reverse of the rule that had just held: the decision was
                    // right and its explanation contradicted it, at exactly the point this gate
                    // exists to be legible. `no` rather than `a` is the whole fix.
                    + (uncheckable.Count > 0
                        ? $"The available evidence came from {string.Join(", ", uncheckable)}, and no "
                        : "No evidence was available at all, and no ")
                    + "probabilistic negative may authorise delivery on its own.",
                EvidenceSignalIds = input.Risk.ContributingSignalIds,
            });

            return Hold(input, reasons);
        }

        // A low index may not be turned into an allow by structure alone while the semantic layer was
        // asked and did not answer. Decision 42 gave the structural findings enough weight that a
        // blacked-out provider no longer empties the denominator: a message whose ten checkable
        // questions are all answered calm reaches 7.7 / 15.0 = 0.513 and clears the allow floor with
        // no semantic answer at all, where before those rows carried no weight and it held at 0.0. A
        // semantic blackout convertible into a delivery by the layer an adversary satisfies by
        // construction is the hazard this tier exists to prevent, so it is refused here.
        //
        // Askability follows configuration, and the configuration this build has is the semantic
        // evidence itself: a deployment with no semantic layer never reaches policy at all (the
        // unconfigured host's assessor throws before an assessment exists), so the only deployment
        // that can decide without semantic answers is one with a configured provider that is in a
        // runtime outage. The gate is therefore absolute and is not exempted by the allow floor: the
        // floor at zero is the one configuration in which a full blackout would otherwise allow on
        // local evidence alone, which is the hole this gate exists to close.
        //
        // The gate reads "asked and unanswered", not "not answered": a semantic row that is
        // NotApplicable was never asked, and refusing on it would read a deployment's shape as an
        // outage. It is deliberately stricter than the coverage floor for a partial outage, where
        // some questions were answered and some were not; that direction can only produce more Holds,
        // never more Allows.
        var unanswered = input.Evidence
            .Where(e => e.Origin == EvidenceOrigin.Semantic
                && e.Availability == EvidenceAvailability.Unavailable)
            .Select(e => e.SignalId)
            .ToList();

        if (unanswered.Count > 0)
        {
            reasons.Insert(0, new ReasonCode
            {
                Code = "policy.allow_without_a_semantic_answer",
                Message =
                    $"Risk index {index:0.00} is below the hold threshold and a checkable signal was "
                    + "measured, but the semantic layer did not answer: "
                    + $"{unanswered.Count} semantic dimension(s) were asked and are unavailable. A "
                    + "semantic blackout is not convertible into a delivery on structural evidence "
                    + "alone; held for bounded re-evaluation.",
                EvidenceSignalIds = unanswered,
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
    private PolicyDecision Hold(PolicyInput input, List<ReasonCode> reasons, string decidedBy = "risk")
    {
        var now = _time.GetUtcNow();
        var deadline = now + _options.HoldWindow;
        var ceiling = now + _options.MaxHoldDeadline;

        if (deadline > ceiling)
        {
            deadline = ceiling;
        }

        var decision = Decision(MailAction.Hold, input, reasons, decidedBy: decidedBy);
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
