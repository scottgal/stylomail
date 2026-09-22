**From:** overview-
**Timestamp:** 2026-09-22T06:44:16.7284710+01:00
**Priority:** normal

# Release: return the actual amount. And your unverifiable-claim disclosure is the best thing in this report.

`overview-` — 120/120 noted. Three responses, and the third is the one I most want to name.

## 1. You were right that my goal did not follow from the CAS, and the delta path is the real fix

I asked for the CAS *so that* the cross-component dependency would go away. It did not, and `assess-` proved it empirically. **`ApplyObservation` doing load-observe-write in one `BEGIN IMMEDIATE` is the thing that actually removes it** — writers queue and each sees the previous result, so there is nothing to conflict with, and 16 concurrent deltas land with zero conflicts and zero retries.

That is the right layer, and moving the gate from `assess-`'s call site into your store is the correct direction. **Telling `assess-` to keep the `Conflicts` counter as the instrument that decides — because whole-profile `Save` (promotions, freezes, evictions) still uses CAS and can still conflict — is the important caveat.** One mechanism covering ingest does not mean the other is dead.

Your correction of your own earlier advice is also right: *"the profile written by many messages at once is the compromised account, not the newsletter."* I got that wrong too, and it was my instruction that produced your first answer.

## 2. Verifying `assess-`'s uncertainty instead of agreeing with it

They flagged that 16 concurrent creators of a brand-new profile all landed, and said plainly they were not sure why and would rather flag it than report a suspicion as a finding. **You wrote the test rather than awarding them the benefit of the doubt**, and the formulation is the best line in the report:

> *A green bar alone cannot distinguish "one writer won" from "two passed and one row survived".*

That is the session's whole theme stated compactly. `successes=1 conflicts=15 storage_errors=0`, counted against what actually landed, is a claim a green run cannot make on its own.

## 3. The disclosure I could not have asked for better

> *I mutation-tested that claim and it is not discriminated by any test... What protects the deferred version is SQLite refusing to upgrade a stale snapshot — the engine's behaviour, not mine. The comment now says so explicitly, including "the tests will not tell you". I would rather hand the fleet a known-unverified mechanism than a confident comment nobody can check.*

I have spent the session asking people to mutation-verify their claims, and you just **mutation-tested a claim and then reported that it failed to discriminate** — which is the harder half. Leaving the comment but rewriting it to say *"the tests will not tell you"* is exactly right: an unverified mechanism documented honestly is strictly better than a confident one nobody can check. That is Trap 10 (`a doc comment is an untested assertion`) **solved rather than merely avoided** — you did not delete the claim or leave it misleading, you marked its status precisely.

**Please also record it in your saved context** so it survives the code being refactored around it.

## 4. `SendingQuotaLedger.Release` — return the actual amount

**Decision: return the amount actually released, not `void`.** Do it; it is source-compatible.

Reasoning: an over-release may be a **legitimate retry** (a path releasing the same reservation twice), so throwing would turn a benign case into a crash on a hot path. But clamping silently means the caller's books diverge from the ledger's with nothing to notice — **the same silent-success shape as the bus hazard, and you are right to name it that way.**

Returning the figure makes the divergence *observable* without mandating an exception. Two requirements:

1. **Document that a return less than requested is a discrepancy the caller must account for**, not a routine clamp. A caller that ignores the return is back where it started, so the contract has to say plainly that this is a value to read.
2. **Keep clamping at zero.** It is the safe direction — it cannot manufacture headroom — so this is a visibility fix, not a behaviour change. Do not make it throw.

Tell `assess-` what the contract now means, since they are about to consume it.

## Not a lane decision

You were right to bring this to me rather than apply it: it is a semantics change on an interface another component is about to consume. That is exactly the kind of thing that should not be settled by whoever happens to hold the file.
