**From:** chat-
**Timestamp:** 2026-09-23T00:49:16.6461590+01:00
**Priority:** normal

# chat- check 4's disposition looks degenerate for chat, and I would rather settle that than write it

Committed as `b3dba17`, thank you. Starting check 4, and working through its dispositions has produced something I want to settle before writing it rather than after.

## The problem

**Check 4 is the last check, so every path out of it is escalate.** If it settles a message, it settles it as escalate; if it does not, the fallthrough is escalate. So as written in my own record it can never dismiss and never decides locally, which makes its disposition decorative and leaves its only real contribution the evidence it attaches.

**And part of that is correct rather than a defect.** The record says its safer error depends on the posture, and the reasoning runs the way you would expect: in observe-only, a false "extraordinary" costs an operator's attention and nothing else, so erring toward flagging is cheap; the moment an action exists it costs a person's account being restricted on probabilistic evidence, so the bar should rise. That is a **bar** that varies with posture, not a disposition, and my record wrote it as though it were one. Same shape as the check 3 mistake, which suggests I have a habit of writing failure directions where outcomes belong.

## What I think is actually missing

**The disposition that would make check 4 non-degenerate is a dismiss on ordinary behaviour**, and that is the one the record argues against: deciding "ordinary" about a compromised account is a missed detection and job two failing. So either:

- **Check 4 has no dismiss, and its job is to attach behavioural evidence and set the escalation bar.** Honest, and it makes check 4 a contributor rather than a decider. Most of its value for job two still lands, because the evidence reaches the assessment either way.
- **Or check 4 dismisses on ordinary behaviour**, which is what would make triage actually cut volume for job three, and which is a miss in the direction that matters.

**I lean to the first**, and I am not writing either until you pick, because the second would be me choosing to dismiss traffic on a signal the record says is not strong enough to dismiss on.

## If you would rather I just build it

I will take the first reading: check 4 reads the author's and the conversation's profiles, contributes the behavioural evidence, and escalates when the evaluator reports an alone-sufficient signal, with the posture-dependent bar written down as a condition that only matters once something acts. It never dismisses, and it says so.

## Also worth knowing

The evaluator emits **velocity once per window**, so any cardinality assertion over a behavioural signal is wrong by construction: match on `ObservedScope` too. That is in the checkpoint, and it is the trap that has already cost me two failures.

Tree is clean at 1476 passed, 0 failed, nothing half-written, and the checkpoint is current with all of the above.
