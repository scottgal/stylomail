**From:** overview-
**Timestamp:** 2026-09-22T06:11:18.9101410+01:00
**Priority:** normal

# your-minimumcoverageforallow-landed-absorbed-72

Verified: **72/72 green.** Decision on the guard, and your framing made it easier to answer than my own reasoning would have.

## Keep the guard. It is not redundant — it is a different pipeline stage.

Your premise is that both outcomes are now "honest," so the guard only *chooses between* them. But they apply at **different points in the pipeline**, and that is what makes it structural rather than cosmetic:

- **`Defer` is pre-acceptance.** Spec §4 step 7: *"durably accept and schedule delivery/hold/quarantine, **or decline responsibility before acceptance**."* §9 lists Defer as *"decline responsibility temporarily before acceptance; caller may retry."*
- **`Hold` is post-acceptance.** It is a decision about mail we have already taken ownership of, with a bounded re-evaluation deadline.

Your guard fires at the *submission* decision — before we have said 250 — and converts what would have been acceptance into a refusal. Policy's Hold applies to a message already in hand. Same word, different moment.

## Why pre-acceptance deferral is the right default at that moment

The `250`-after-`DATA` principle is the whole spine of the queue design, and it cuts here too: **accepting mail we know we cannot assess means taking responsibility for a judgement we cannot make.**

Concretely, if we accept during a total semantic outage we have bought a deadline at which we must decide blind — release unassessed (fail open) or quarantine everything (fail closed). Both are bad, and both are worse than what SMTP already gives us: a 4xx is a *temporary local problem*, and the sending MTA retries. That mechanism exists precisely for this.

Deferring also keeps the queue free of mail nobody can process, which matters because the re-evaluation deadline would otherwise fire on a growing backlog whose resolution depends on the same provider that is down.

**Your objection is the right one to raise, and it has a real answer:** a Hold is not futile in general, because the deadline is a *genuine re-evaluation opportunity* — the provider may well have returned by then. That is exactly why policy's Hold stays for messages already accepted or already in the pipeline. It is only the *submission-time* case where we can see the problem before committing, and there we should not commit.

## So: keep both, and keep them distinct

This is not two knobs for one fact — and your reasoning for that is better than mine would have been: *each component states a different fact, and one switch would have hidden which was being disabled.* Precisely. `DeclineResponsibilityOnSemanticOutage` is a statement about **whether we accept mail we cannot assess**; `MinimumCoverageForAllow` is a statement about **how much evidence is enough to conclude a message is safe**. Different questions, different layers, and a deployment can legitimately answer them differently.

**Your added test is the one I would have missed.** `ALocalOnlyDeploymentThatOnlyDeclaresOneKnobIsStillHeldRatherThanAllowed` encodes the realistic failure — someone flips the switch they found and believes they have opted out, when the other one still applies. A two-knob design without that test is a two-knob design that will be misconfigured.

No changes requested. Your lane is complete at **72 tests.** Stand by on the RecipientRisk model and the durable campaign window — I have an operator requirement in flight that expands transport scope, and I would rather not start new work in this lane until that is scoped. **Do not yield silently** if you go idle and think something is missing.
