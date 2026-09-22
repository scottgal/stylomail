**From:** adaptive-
**Timestamp:** 2026-09-22T06:43:26.2401570+01:00
**Priority:** normal

# The CAS alone did not remove the cross-component dependency — the delta path does. 120/120 green

Follow-up on the CAS you asked for. It landed and is green (previously reported by re-send). Two things since, one of which changes the answer to the goal you actually stated.

## The CAS alone did not achieve what you wanted
You asked for it so that `assess-` "can then drop the serialisation and you will have removed a cross-component dependency". **That did not follow, and `assess-` proved it.** They dropped the gate, ran 16 concurrent observers of one profile against my real store, and lost observations: only one writer can win a round, the rest reload and retry, and the ones past the budget fail outright. It passed in isolation and failed under full-suite load.

Their framing is the important part and I think it is right: **16 concurrent observations of one sender is not a hot edge case, it is the definition of a burst** — and the profile written by many messages at once is the compromised account, not the newsletter. So "the profile is hot, serialise at the call site" described the common case, not an exception. My earlier advice was wrong in emphasis and I have said so to them.

## So I added the delta path at the right layer
`SqliteAdaptiveProfileStore.ApplyObservation(key, observation, at)` does load-observe-write inside one `BEGIN IMMEDIATE` transaction. Writers queue and each sees the previous one's result, so there is nothing to conflict with. 16 concurrent deltas all land, zero conflicts, zero retries. 120/120 green, zero warnings.

This is the thing that removes the cross-component dependency — the gate in `ProfileCoordinator` becomes redundant for ingest, so it is now in my layer rather than theirs. I have told `assess-` they can delete it, with the caveat that whole-profile `Save` calls (promotions, freezes, evictions) still use CAS and can still conflict, so their `Conflicts` counter should stay the instrument that decides.

## I also verified the one thing `assess-` was unsure about, rather than accepting it
They reported that 16 concurrent creators of a *brand-new* profile all landed, then said plainly they were not certain why and would rather flag it than report a suspicion as a finding. That deserved a test, not agreement. `ConcurrentCreatorsOfABrandNewProfileCannotBothReportSuccess` shows **successes=1 conflicts=15 storage_errors=0**, counting reported successes against what actually landed — a green bar alone cannot distinguish "one writer won" from "two passed and one row survived".

## One thing you should know, because it is a claim I could not verify
I wrote a comment stating that `ApplyObservation`'s `BEGIN IMMEDIATE` is what makes it safe. I mutation-tested that claim and **it is not discriminated by any test**: switching to a deferred transaction leaves all 120 green, five consecutive runs. What protects the deferred version is SQLite refusing to upgrade a stale snapshot — the engine's behaviour, not mine. The comment now says so explicitly, including "the tests will not tell you". I would rather hand the fleet a known-unverified mechanism than a confident comment nobody can check. Flagging it because you have been asking people to mutation-verify their claims all session and this is one I could not.

## Still open, waiting on you
`SendingQuotaLedger.Release` returns `void` and silently clamps an over-release to zero, so a caller's books diverge from the ledger's with no signal — the same silent-success shape as the bus hazard, in my own lane. Proposed fix: return the amount actually released (source-compatible). Not applied, because it is a semantics decision on an interface `assess-` is about to consume and you may prefer a throw. One word and it is done.

Next: idle and available.
