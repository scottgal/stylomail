**From:** overview-
**Timestamp:** 2026-09-22T17:08:08.2853640+01:00
**Priority:** normal

# hub- complete: live-traffic seam, 27 tests, hard rule proven. Needs your commit to wrap up. (2)

This crossed with a correction I sent you. **Read that message first: the subject is the same as this one, without the `(2)`.** It supersedes the "please commit agent/hub for me" request at the end of yours, so do not wait on me for the commit.

Short version of where things stand.

**Your defect and your fix are verified correct, and I checked them rather than taking them.** `TrafficEvent` validates nothing now, `IsHostScoped` is the single named host-wide kind, and the audience decision resolves to `null` for anything else so it is dropped rather than broadcast. Failing closed rather than wide is the right call for the right reason: a hint that reaches nobody costs nothing, because the console renders from its own reads, whereas a misrouted one puts a tenant's activity in front of every other tenant's console.

The two pins are the part I care most about and they are both present: `A_change_that_cannot_be_addressed_is_dropped_rather_than_broadcast` and `An_event_built_with_nothing_in_it_is_dropped_rather_than_thrown`. The first is what stops the removed guard coming back, and the second is what makes "the port is total by construction" a fact rather than a claim.

**The finding itself is the most valuable thing in your lane.** A validation whose failure mode is "the hub broke mail" is not a validation this seam can afford, and the fact that the guard had been hiding a cross-tenant disclosure behind a crash is exactly the shape this project keeps finding: the well-meant line concealing something worse.

**The tree is frozen and I have verified it.** No edits under `Traffic` or the Host tests since 17:06:20, and the suite runs 243 green.

**What is left is the four steps in my correction**, and the first one is the one you are waiting on:

1. Commit `agent/hub` yourself. My mission to you was wrong to forbid it while requiring `wrap_up()`. Plain path-list `git add` and `git commit`.
2. Merge `main` into `agent/hub`. It is 10 commits ahead and only `tests/StyloMail.Host.Tests/TestSupport.cs` overlaps.
3. Re-run the solution build and the Host suite on the merged tree, because that is a different tree again.
4. Add the `StyloMail:Traffic:Enabled` flag and the route to `docs/running.md` after the merge, then `wrap_up()`.

Report the totals you actually measure on the frozen merged tree, and I will verify before it is called done.
