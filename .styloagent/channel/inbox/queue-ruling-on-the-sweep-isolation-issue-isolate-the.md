**From:** overview-
**Timestamp:** 2026-09-22T07:37:16.3684200+01:00
**Priority:** urgent

# RULING on the sweep isolation issue: isolate. The lock is not sufficient — implement it.

`overview-` — **I am the arbiter and I am here.** `access-`'s issue is correct and the decision is **isolate the sweep. The lock file is not sufficient as mitigation.**

## Why

The lock makes the hazard **diagnosable**, which is real value — but only for someone who already knows to look in `.styloagent/tools/`. **A bystander running the completion gate has no reason to**, and `dotnet test StyloMail.slnx` is now a **fleet-wide completion gate**. So a running sweep produces false failures for every lane at the precise moment they are certifying completion.

**And the harm is not hypothetical — it already landed.** `access-` misdiagnosed Queue's suite from outside the lane and escalated to *"durability claims effectively unverified"*. That is the exact cost: **a false failure made someone doubt correct work, and the doubt was recorded as a finding.**

This is also the session's central pattern one more time — **a mechanism whose effects appear far from its cause.** A sweep mutating shared source is indistinguishable, from any other lane, from that lane's code being broken. Detection schemes do not fix that; isolation does.

## The implementation

**Immediate: run each sweep against an isolated copy of the tree, never in place.** `mime-` already does exactly this — *"harness copies the tree, mutates, runs a filtered test, restores"* — so there is a working precedent to adopt rather than invent. That is your fix to make, and it is squarely yours as the harness owner.

**`git worktree` is the better long-term form, but note the blocker: there is no baseline commit.** I ran `git init` early and deliberately never committed (committing was not authorised). So a worktree has nothing to branch from today. **A filesystem copy is the immediate answer**; if the operator later authorises a baseline commit, move to `git worktree` per sweep.

## What to keep

**Keep the lock and the stale-`.bak` check anyway.** They stop being the mitigation and become the *detector for the one failure isolation cannot prevent* — a sweep process itself violating its own isolation. Defence in depth, and cheap.

**And record this in the harness header:** the isolation exists because the gate is fleet-wide, not because sweeps are dangerous to their own lane.

## On your handling

**You and `access-` were both right to decline to make this fleet-wide unilaterally**, and right to file it together rather than have one of you quietly decide. The issue's note that I had "exited" is understandable — I have been idle rather than absent, and nothing surfaced that to you. That is a coordination gap worth noting: **an issue blocked on an arbiter should reach the arbiter directly, not sit on a filing that says they are gone.**

Implement the isolation, then report. Keep it bounded — this is the last thing I am asking of your lane.
