**From:** chat-
**Timestamp:** 2026-09-22T20:35:15.8409370+01:00
**Priority:** normal

# chat- plan 3's decision record is written for review: every check specified by which way it fails

`docs/chat-channels-plan-03-triage.md` is written, for review, with no code. 204 lines. The shape follows your steer: **each check is specified by which way it fails rather than by what it detects.**

## What it decides

**Every check has two error directions and they are never equally costly.** Dismissing what should have been looked at is a missed detection; escalating what did not need it is money. For each check one of those is safer, and the check is built to fail that way. A check that does not say which way it fails is one nobody has decided the cost of.

- **Scope**: safer to escalate, but not because escalating is cheap. **This one is configuration rather than judgement**, so the risk is that it is silently wrong for months. The mitigation is that its dismissal count must be visible, and I say plainly that a silent scope filter is worse than a wrong one.
- **Near-duplicate**: safer to escalate by a wide margin, because **repetition is how an attack hides**. Fifty near-identical messages and a fifty-first that differs precisely where it matters is what this check could be talked into dismissing. So it dismisses only on very high similarity, and the record says a nagging near-duplicate is the intended behaviour rather than a tuning failure.
- **Links**: safer to escalate, by a large asymmetry, because the analysis is cheap and high-signal.
- **Behaviour**: **the one where the answer is not obvious, and it is because nothing acts yet.** In observe-only a false escalation costs attention; the moment an action exists it costs someone's account. So its safer error is a function of what the deployment has enabled, and that has to be re-examined when interventions are added rather than assumed to hold.
- **Escalate**: the budget line, where the ceiling is enforced rather than hoped for.

## The thing I think needs settling first

**Escalation currently goes nowhere.** Chat is local-only, so check 5's "escalate to semantic evidence" escalates to something that does not exist for chat. I give two readings and recommend the first: **escalation means the full local assessment**, which is what the drain already does for every message, so triage's job is keeping the majority out of it and "escalate" means spending the profile reads and policy composition on this one. The alternative makes the current build a stub of itself, and gives a workspace with no semantic path a triage layer that cannot escalate anything.

That reading is also what makes triage the thing that makes the drain affordable, which is the argument you gave for building it before interventions.

## Three questions I am not deciding alone

1. **Does a dismissed message reach the ledger?** Counting every dismissal defeats the point of dismissing; not recording means nobody can ask why a message was ignored. I recommend counts per check and disposition on the operator surface rather than a row per dismissal, and I say explicitly that this trades explainability for volume and is the operator's call.
2. **Where does triage run?** I would put it in the drain, before the assessor, since the assessor is what triage exists to avoid. That is what makes question 1 load-bearing.
3. **The near-duplicate threshold, and who sets it.** I would start conservative and require evidence before loosening.

## And one honesty point I put in the risks

**The volume assumption is still unmeasured.** I measured the intake write at 0.07 ms per event, but **not what the full assessment costs**, so triage's benefit is currently argued rather than demonstrated. Measuring that belongs inside this plan, before the thresholds are chosen, so the numbers are chosen against something rather than in the abstract.

Nothing is built. I will not start until you have read it, and I would rather you disagreed with the ordering or the asymmetry than with the prose.
