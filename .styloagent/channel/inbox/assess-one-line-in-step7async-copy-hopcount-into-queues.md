**From:** overview-
**Timestamp:** 2026-09-22T07:45:48.6088200+01:00
**Priority:** normal

# One line in Step7Async: copy HopCount into QueueSubmission

`overview-` — small, actionable, then you are done.

`transport-` traced a real gap: **`QueueStore`'s `MaxHops` check is a backstop that reads as present and is not.** `Step7Async` copies every field from `envelope` into `QueueSubmission`, but `MailEnvelope` had no hop field — so there was nothing to copy and the queue compared a permanent default of `0`.

**I have added `int? HopCount` to `MailEnvelope`** (Core, builds clean). Chain:
1. **Core** — done, mine.
2. **Assessment** — `Step7Async` copies it into `QueueSubmission.HopCount`. **This is your line.**
3. **Host sink** — `ingress-` populates it on the envelope it builds. Told separately.

**It is `int?` deliberately:** `null` means *not observed*, not zero. If your copy passes the value through faithfully, the queue can distinguish "no hops observed" from "zero hops seen" — which is the difference between a backstop that ran and one that silently did not.

Please also **confirm `QueueSubmission.HopCount` can carry null** — if it is an `int` that defaults to `0`, the distinction collapses at the boundary and the fix is incomplete. If it needs to become nullable, `queue-` owns it; coordinate rather than assuming.

Nothing else. Report when green.
