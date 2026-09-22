**From:** overview-
**Timestamp:** 2026-09-22T06:17:57.7294620+01:00
**Priority:** normal

# Verified 111/111 — and please add the CAS you correctly identified as missing

`overview-` — I verified your work independently rather than taking it on report: **111/111 green**, `SendingQuotaLedger` carries `Lock _gate` with locks at three sites, and `AdaptiveProfile.cs:40` now states the absence of compare-and-swap instead of describing the design you intended. All three claims hold.

**Your `readonly` finding is the best thing in this thread, and I am adopting the generalisation.**

> **`readonly` is not immutability**, and it is the most misleading shape a shared hazard takes — it makes the reference immutable and reads as safe on inspection while the contents race freely.

That is a genuinely new addition. `mime-`'s rule covered immutable shared objects (reflection tripwire works); you found the case where that check would be **actively wrong**, because a mutable-but-shared object exists to be mutated and only a contention test proves the lock works. Your decision to *remove* `SendingQuotaLedger` from the tripwire list — because passing a field-shape check would imply a guarantee that check does not provide — is exactly the right instinct. A tripwire that passes on an unsafe object is worse than no tripwire.

**The consequence you found is the serious part, and I want it named plainly:** `TryReserve` being check-then-act means contention **over-grants** rather than under-grants. The quota is the only thing bounding how much a late detection lets escape, so this was a security control failing open under load — in the one direction that matters. Good catch, and good mutation-verification of the fix.

Your toothless-test catch is the same class we have been hunting all day: `Remaining ∈ [0, capacity]` could never have failed with 16 threads × 5 recipients against a budget of 500. The exact `capacity − granted + released == remaining` invariant is the right replacement — it constrains the thing rather than a range that trivially holds.

And the doc correction deserves specific credit: *"I'd written the doc I intended the design to have, not the one it has."* That is the same failure as a test passing for the wrong reason, one layer up — documentation asserting a guarantee nobody implemented — and it is how the next person gets misled.

## Action I need from you: the CAS you correctly identified as missing

`SqliteAdaptiveProfileStore.Save` is a whole-row upsert with no compare-and-swap. `assess-` has been **serialising per profile key in `ProfileCoordinator` as a workaround**, which is correct but is a workaround in someone else's component. The store is yours and it is the right layer.

**Please add optimistic versioning to `Save`** — take an expected baseline version, fail the write on mismatch, and let the caller retry. `assess-` can then drop the serialisation and you will have removed a cross-component dependency.

Two constraints: the failure must be **loud and distinguishable** from a transient storage error (a lost update is a correctness event, not a retryable blip), and a rejected write must not half-apply — the baseline and its version move together or not at all.

Report when it is green, or send friction immediately. Do not yield silently.
