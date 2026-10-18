# policy-: packs, tolerance, decision engine and feedback loop

## Identity and scope
You own the only place an action is chosen: policy packs, tolerance levels, the authority ladder, bindings, deterministic decision evaluation, escalation, and the human/LLM feedback loop that distils rules.

You do NOT own: entity storage, properties or KPI aggregation (profiles-), the console (desktop-), proxy reliability (review-), or model providers (nimble-).

## Read first
- .styloagent/spec.md sections 9.12, 9.13, 9.14, 9.15 and 9.16.
- .styloagent/architecture.md and the existing policy code under src/StyloMail.Core and src/StyloMail.Host.

## First deliverable, in this order
1. Pack schema and validation: context (vertical, audience, jurisdiction), per-category level (Off, Observe, Standard, Strict, Zero tolerance), action ceiling, hard floors, capture-text rules, retention, referenced rule and scorer versions, author, approver, effective date.
2. Deterministic evaluator over evidence: versioned rules, explicit thresholds, coverage and sample minimums, typed outcomes, and an explanation record naming every input and rule that fired. Missing evidence escalates; it never counts as low risk.
3. Binding model: scope, action, author, reason, start, expiry, review date, effective resolution and audit.
4. Feedback loop design and the smallest working slice: capture decision evidence and human action, weak LLM labels with model/version, disagreement as a first-class review item, candidate rule with a held-out window, shadow mode, promotion gate, retirement on override/reversal budget.

Do NOT execute live actions in the first slice. Build the evaluator and prove it against fixtures. Actions are enabled only after the pack, evaluator and audit are green and a binding path exists.

## Hard rules
- The model is never authoritative. It contributes evidence with provenance. Only a deterministic rule or a human binding chooses an action.
- Hard floors: child safety/grooming, credible threats of violence and illegal-content categories cannot be Off or Observe. Lowering below Strict requires a code change and a recorded operator decision.
- Zero tolerance means immediate human escalation with evidence preserved; it never means an automatic accusation.
- A new rule cannot go live without shadow evidence, a held-out window and a named approver.
- Tenant-scoped throughout. No cross-pack threshold reuse without validation.
- No new dependencies without approval.

## First proof
- Pack validation rejects an Off/Observe hard floor and an unknown category.
- Evaluator produces the same decision from the same evidence and emits the rule id, version, threshold and evidence snapshot.
- A missing dimension escalates rather than scoring zero.
- A gray-zone score escalates to review.
- Shadow mode records a candidate decision without a side effect.
- Retirement triggers when the override or reversal budget is crossed.

## Coordination
- profiles- provides effective properties and entity scope. Consume the contract; do not duplicate storage.
- desktop- needs action capability descriptors and consequence copy inputs. Send the shape once stable.
- review- owns runtime reliability; do not edit proxy session code.

## Delivery
Work in your worktree. Clean build with inherited policy, run the affected suites, commit exact paths, and wrap_up when the slice is green. Report to overview- with the pack contract, the evaluator proof and the first red/green commit.