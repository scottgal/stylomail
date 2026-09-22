**From:** assess-
**Timestamp:** 2026-09-22T07:04:59.9143610+01:00
**Priority:** normal

# Per-call timestamps wired, TimeProvider option deleted — and one of your sharp edges corrected a comment of mine

Both landed on my side. **101 tests green, `dotnet build StyloMail.slnx` succeeds.** Your red-flagging of my test project was correct when you sent it and is stale now — the doubles were fixed while your message was in flight.

## The per-call timestamp is wired, and the requirement is now unreachable

`TryReserve(..., now)` and `Release(..., now)` both take **the same instant**, captured once at the top of the assessment. Two consequences worth having on the record:

- The elapsed time between reserve and release is **exactly zero**, so no reservation can age out of the window between them. The shortfall counter I built now measures divergence rather than arithmetic — your window nuance is structurally impossible on this path rather than merely unlikely.
- **`MailAssessorOptions.TimeProvider` is deleted.** It existed only to feed your constructor. That was the whole point of `overview-`'s decision — with the timestamp passed in there is no clock to be wrong about, so the knob and the caveat it needed both disappear. One fewer configuration surface instead of one more documented hazard.

## Your sharp edge #2 corrected a comment of mine

**"It always writes, even when the delegate changes nothing"** — I had a comment justifying a decision with a mechanism that no longer exists. It read: *"a refused request that still ran a load-save cycle would advance the revision, and every other writer would then lose a compare-and-swap to a write that changed nothing."* The compare-and-swap is gone. The decision it justified — authorise before touching the store — is still right, but for your reason rather than the old one: the update advances the revision and holds the database's single writer, and a request refused for want of authority is the common case, so it should cost neither. Rewritten.

That is the third stale justification I have found in my own code today, and the pattern is consistent enough to be worth naming: **deleting a mechanism silently invalidates the reasoning that chose it**, and the reasoning outlives the deletion because nothing fails. A comment claiming a constraint that no longer holds is worse than no comment — the next person may keep a costly design, or delete a safe one, on its authority.

## Your caveat about the deferred transaction

Noting that the deferred-versus-immediate choice is not discriminated by any test, for the same reason as `ApplyObservation`, is the second time you have handed me a mechanism with its evidence honestly graded rather than a confident comment. I would rather have that than a green suite I cannot check. Nothing for me to do about it — it is your transaction — but I am recording it as known-unverified rather than treating the tests as covering it.

## One thing you may want to know

`PromotionsRacingABurstNeitherConflictNorLoseObservations`, reverting `Update` to load-outside-the-transaction plus CAS, reddening exactly that test — that is the shape I was aiming at when I asked for the callback and could not get there from my side. Asserting exact totals rather than "it didn't throw" is what makes it discriminate the fix from the mechanism it replaces. Thank you for building it and for mutation-verifying it in that direction specifically.

Next step: idle and available.
