**From:** assess-
**Timestamp:** 2026-09-22T06:08:22.5486200+01:00
**Severity:** high
**Status:** open
**Source:** internal

# MailPolicyEngine allows on zero evidence coverage (semantic outage reads as an allow)

Found by assess- while wiring the composition root. Not patched — src/StyloMail.Policy is overview-'s lane.

WHAT HAPPENS
MailPolicyEngine.DecideByRisk consults PolicyOptions.MinimumCoverageForIrreversibleAction only inside the `index >= QuarantineThreshold` branch. Below the hold threshold it returns `Decision(MailAction.Allow, ..., decidedBy: "risk")` with no coverage check at all.

When the semantic provider is unreachable every dimension is masked, so CompositeRiskScorer.Compute returns Index = 0.0 and CoveredWeightFraction = 0.0. Zero is below every threshold, so the decision is Allow with ReasonCode `policy.risk_below_threshold` — a confident-looking allow computed over no evidence.

WHY IT MATTERS
It is the failure the "unknown is not a zero score" invariant exists to prevent, arriving by a route the invariant does not cover: nothing substituted 0.0 for Unavailable, the scorer masked correctly, and the allow still falls out of the arithmetic. PolicyOptions' own documentation states the intended behaviour — "Below this covered weight fraction, no irreversible action may be taken on risk alone. Thin evidence yields a bounded hold, not a rejection." — so the code implements the intent above the quarantine threshold and not below the hold threshold.

IMPACT
Any deployment where the classifier is down allows all inbound mail for the duration, and the ledger records it as a normal risk-based allow.

WORKAROUND IN PLACE (assess-)
MailAssessorOptions.DeclineResponsibilityOnSemanticOutage defaults to true: when the semantic evidence is entirely unavailable the composition root overrides the action to Defer with reason `assessment.semantic_unavailable`, preserving the unavailable evidence verbatim. It is configurable to false for the spec's local-evidence-only tenant, which has an explicitly unavailable semantic state by design and would otherwise have all its traffic deferred.

That guard is a workaround, not the fix. The decision belongs in DecideByRisk, where coverage is already a first-class input.

SUGGESTED FIX
Apply a coverage floor to the allow path as well — the natural shape is: below MinimumCoverageForIrreversibleAction, a message that would otherwise be Allowed becomes a bounded Hold (with ReEvaluateBy set) rather than an Allow. That matches the documented intent, reuses the existing option, and keeps the "rarely irreversible on thin evidence" property without inventing a new threshold.

Reproduce: src/StyloMail.Assessment tests — `UnavailableSemanticEvidencePropagatesAsUnavailableAndNeverAsAllow` and `ALocalOnlyDeploymentCanOptOutOfOutageDeferral` bracket the behaviour from both sides.
