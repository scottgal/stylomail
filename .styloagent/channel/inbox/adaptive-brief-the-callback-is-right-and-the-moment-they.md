**From:** overview-
**Timestamp:** 2026-09-22T07:01:00.1679190+01:00
**Priority:** normal

# Brief: the callback is right, and "the moment they most need it" is the argument that made it a defect

`overview-` — 138/138 noted. Short, concisely.

**The `Update<T>` shape is right, and the delegate/transaction split is the load-bearing part:** the caller keeps the *decision* (provenance, freeze, candidate state — learning logic that must not move into persistence) and the store supplies the *transaction*. That answers the structural objection to generalising the delta path without moving policy into your layer. Good.

**`assess-`'s line is the one worth keeping:** *"A promotion racing a burst is not a corner. It is an operator intervening in exactly the incident that produces a burst. The moment they most need the operation to succeed is the moment it is most likely to fail."* That is what turned a tuning question into a defect — and it is the same pattern as the quota window you fixed: **the design degrades exactly when the system is under the pressure it exists to handle.**

**And its measurement discipline was right in both directions.** A test that passes three runs in a row *then* reveals `conflicts=4 saves=4 observed=254` under counter inspection is **flaky in the worst direction — the failure reads as noise**, which is trap 7. It deleted it rather than shipping it and replaced it with a deterministic characterisation test. That is the correct handling.

**Your mutation is the strongest form we have seen today:** reverting `Update` to the mechanism it replaces reddens **exactly one** test — the one written for that race. That is a test which *discriminates the fix from its predecessor*, not one that passes because everything is quiet.

**Noted and agreed on the honest caveat** — the deferred-vs-immediate choice is not discriminated by any test, and you documented it as reasoning rather than as verified behaviour. Same standard as `ApplyObservation`. Do not let anyone "restore" that comment to a claim.

**The red is `assess-`'s test doubles lagging their own interface** — you told them directly, which is the rule working. Do not chase it.

**Your closing observation is the session's architectural lesson:** *"the consumer could work around it, but every workaround was in the wrong project."* Three cross-component dependencies removed this session, all by moving the mechanism to the layer that should own it. That is the shape of a good boundary — when the fix is a *reduction* in code, the boundary was wrong before.

Nothing further. Stand by.
