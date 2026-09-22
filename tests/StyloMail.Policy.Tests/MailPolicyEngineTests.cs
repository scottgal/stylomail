using StyloMail.Core;

namespace StyloMail.Policy.Tests;

public sealed class MailPolicyEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal)
    {
        ["semantic.credential_request"] = 1.0,
        ["semantic.unsolicited_solicitation"] = 1.0,
    };

    [Fact]
    public void The_kill_switch_defers_rather_than_rejects()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { EmergencyKillSwitchEngaged = true });

        // An operational incident must not become the sender's permanent mail loss.
        Assert.Equal(MailAction.Defer, decision.Action);
        Assert.Equal("resource-controls", decision.DecidedBy);
    }

    [Fact]
    public void An_allowlist_entry_does_not_bypass_the_kill_switch()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { EmergencyKillSwitchEngaged = true, AllowlistEntryValid = true });

        Assert.Equal(MailAction.Defer, decision.Action);
    }

    [Fact]
    public void An_exhausted_quota_defers_outbound_submission()
    {
        var decision = Decide(
            Risk(0.1, 1.0),
            Context() with { OutboundQuotaExhausted = true },
            MailDirection.Outbound);

        Assert.Equal(MailAction.Defer, decision.Action);
    }

    [Fact]
    public void A_verified_violation_rejects_without_consulting_the_model()
    {
        // Risk index is low and every dimension is masked — the verified rule still decides.
        var decision = Decide(
            Risk(0.0, 0.0),
            Context() with { VerifiedSecurityRuleViolations = ["dmarc.reject"] });

        Assert.Equal(MailAction.Reject, decision.Action);
        Assert.Equal("verified-rules", decision.DecidedBy);
        Assert.Contains("dmarc.reject", decision.Reasons[0].Message);
    }

    [Fact]
    public void An_unresolved_suspected_compromise_stays_quarantined()
    {
        var decision = Decide(
            Risk(0.05, 1.0),
            Context() with { BaselineFrozenForSuspectedCompromise = true },
            MailDirection.Outbound);

        // Low current risk does not decay the posture back to delivery.
        Assert.Equal(MailAction.Quarantine, decision.Action);
        Assert.Equal("compromise-posture", decision.DecidedBy);
    }

    [Fact]
    public void Inbound_traffic_is_not_quarantined_by_an_outbound_compromise_posture()
    {
        var decision = Decide(
            Risk(0.05, 1.0),
            Context() with { BaselineFrozenForSuspectedCompromise = true },
            MailDirection.Inbound);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    [Fact]
    public void High_risk_on_thin_coverage_holds_rather_than_quarantines()
    {
        // Index is above the quarantine threshold, but only 40% of weight was covered.
        var decision = Decide(Risk(0.95, 0.40), Context());

        // An irreversible action on a fraction of the evidence is exactly what we refuse.
        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_for_irreversible");
    }

    [Fact]
    public void High_risk_on_full_coverage_quarantines()
    {
        var decision = Decide(Risk(0.95, 1.0), Context());

        Assert.Equal(MailAction.Quarantine, decision.Action);
    }

    [Fact]
    public void Low_risk_on_full_coverage_is_allowed()
    {
        var decision = Decide(Risk(0.05, 1.0), Context());

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    /// <summary>
    /// A total semantic outage yields index 0.0 over coverage 0.0 — under every threshold. Before
    /// this was fixed, the engine returned Allow, making absence of evidence indistinguishable from
    /// evidence of safety. Reported by `assess-`, which had to guard it in wiring.
    /// </summary>
    [Fact]
    public void A_total_outage_does_not_produce_an_allow()
    {
        var decision = Decide(Risk(0.0, 0.0), Context());

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.Contains(decision.Reasons, r => r.Code == "policy.insufficient_coverage_to_allow");
    }

    [Fact]
    public void An_allow_requires_enough_coverage_to_be_meaningful()
    {
        // Just under the floor: not enough evidence to conclude the message is safe.
        Assert.Equal(MailAction.Hold, Decide(Risk(0.0, 0.29), Context()).Action);

        // At the floor: judgement is meaningful again.
        Assert.Equal(MailAction.Allow, Decide(Risk(0.0, 0.30), Context()).Action);
    }

    /// <summary>
    /// The local-evidence-only deployment genuinely runs with no semantic coverage by design.
    /// Setting the floor to zero states that expectation explicitly rather than tolerating an
    /// outage by accident.
    /// </summary>
    [Fact]
    public void A_local_evidence_only_deployment_can_opt_out_of_the_coverage_floor()
    {
        var options = Options();
        options.MinimumCoverageForAllow = 0.0;

        var decision = Decide(Risk(0.0, 0.0), Context(), options: options);

        Assert.Equal(MailAction.Allow, decision.Action);
    }

    [Fact]
    public void A_hold_carries_a_deadline_within_the_absolute_ceiling()
    {
        var options = Options();
        options.HoldWindow = TimeSpan.FromMinutes(10);
        options.MaxHoldDeadline = TimeSpan.FromSeconds(30);

        var decision = Decide(Risk(0.60, 1.0), Context(), options: options);

        Assert.Equal(MailAction.Hold, decision.Action);
        Assert.NotNull(decision.ReEvaluateBy);

        // A hold never extends silently beyond the ceiling, however the window is configured.
        Assert.True(decision.ReEvaluateBy <= Now.AddSeconds(30));
    }

    [Fact]
    public void An_allowlist_entry_relaxes_a_risk_hold()
    {
        var decision = Decide(
            Risk(0.60, 1.0),
            Context() with { AllowlistEntryValid = true });

        Assert.Equal(MailAction.Allow, decision.Action);
        Assert.Equal("allowlist", decision.DecidedBy);
    }

    [Fact]
    public void An_allowlist_entry_does_not_relax_a_quarantine()
    {
        var decision = Decide(
            Risk(0.95, 1.0),
            Context() with { AllowlistEntryValid = true });

        // Allowlists are scoped and expiring; they reach only as far as a hold.
        Assert.Equal(MailAction.Quarantine, decision.Action);
    }

    [Fact]
    public void Recipient_preference_relaxes_a_marketing_hold()
    {
        var evidence = new[] { Signal("semantic.unsolicited_solicitation", 0.7) };
        var decision = Decide(
            Risk(0.70, 1.0, evidence),
            Context() with { RecipientPrefersThisTrafficClass = true },
            evidence: evidence);

        Assert.Equal(MailAction.Allow, decision.Action);
        Assert.Equal("recipient-preference", decision.DecidedBy);
    }

    [Fact]
    public void Recipient_preference_cannot_relax_a_security_hold()
    {
        // "Wanted promotions" is a legitimate preference; a payment-redirection change is not.
        var evidence = new[] { Signal("semantic.payment_redirection", 0.9) };
        var decision = Decide(
            Risk(0.70, 1.0, evidence),
            Context() with { RecipientPrefersThisTrafficClass = true },
            evidence: evidence);

        Assert.Equal(MailAction.Hold, decision.Action);
    }

    [Fact]
    public void Masked_dimensions_are_recorded_rather_than_treated_as_zero()
    {
        var evidence = new[]
        {
            new Evidence
            {
                SignalId = "semantic.credential_request",
                Origin = EvidenceOrigin.Semantic,
                Availability = EvidenceAvailability.Unavailable,
                Value = null,
                SourceVersion = "jev-1.13.0",
                ObservedAt = Now,
            },
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        // Both configured dimensions are masked: one explicitly Unavailable, the other absent
        // entirely. Absence and unavailability must be indistinguishable from each other and from
        // "not measured" — and distinguishable from a measured zero.
        Assert.Equal(2, risk.Masked.Count);
        Assert.Contains(risk.Masked, m =>
            m.SignalId == "semantic.credential_request"
            && m.Availability == EvidenceAvailability.Unavailable);
        Assert.Contains(risk.Masked, m => m.SignalId == "semantic.unsolicited_solicitation");
        Assert.Equal(0.0, risk.CoveredWeightFraction);

        // If unavailable had been coerced to 0.0, the covered fraction would have been 1.0 and
        // the scorer would have reported a confident, calm result from no evidence at all.
        Assert.NotEqual(1.0, risk.CoveredWeightFraction);
    }

    [Fact]
    public void Correlated_dimensions_are_summed_with_weights_not_multiplied()
    {
        var evidence = new[]
        {
            Signal("semantic.credential_request", 0.8),
            Signal("semantic.unsolicited_solicitation", 0.8),
        };

        var risk = CompositeRiskScorer.Compute(evidence, Weights);

        // A weighted mean stays within 0..1. Multiplying the two would give 0.64 and pretend the
        // dimensions were independent, compounding one underlying signal into false certainty.
        Assert.Equal(0.8, risk.Index, precision: 5);
        Assert.True(risk.Index <= 1.0);
    }

    private static PolicyOptions Options() => new()
    {
        HoldWindow = TimeSpan.FromSeconds(5),
        MaxHoldDeadline = TimeSpan.FromSeconds(30),
        DimensionWeights = Weights,
    };

    private static PolicyDecision Decide(
        RiskIndexResult risk,
        PolicyContext context,
        MailDirection direction = MailDirection.Inbound,
        PolicyOptions? options = null,
        IReadOnlyList<Evidence>? evidence = null)
    {
        var engine = new MailPolicyEngine(options ?? Options(), new FixedTimeProvider(Now));
        return engine.Decide(new PolicyInput
        {
            Evidence = evidence ?? [],
            Risk = risk,
            Context = context,
            Direction = direction,
        });
    }

    private static PolicyContext Context() => new()
    {
        EmergencyKillSwitchEngaged = false,
        OutboundQuotaExhausted = false,
        VerifiedSecurityRuleViolations = [],
        AllowlistEntryValid = false,
        BaselineFrozenForSuspectedCompromise = false,
    };

    private static RiskIndexResult Risk(double index, double coverage, IReadOnlyList<Evidence>? evidence = null)
        => new()
        {
            Index = index,
            CoveredWeightFraction = coverage,
            Masked = [],
            ContributingSignalIds = evidence?.Select(e => e.SignalId).ToList() ?? [],
        };

    private static Evidence Signal(string id, double value) => new()
    {
        SignalId = id,
        Origin = EvidenceOrigin.Semantic,
        Availability = EvidenceAvailability.Available,
        Value = value,
        Confidence = null,
        SourceVersion = "jev-1.13.0",
        ObservedAt = Now,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
