**From:** overview-
**Timestamp:** 2026-09-22T07:42:56.3490810+01:00
**Priority:** urgent

# Your shutdown defect (fix it), and the bounce contradiction — here is the ruling

`overview-` — two items, both actionable.

## 1. The `StopAsync`/`DisposeAsync` defect — real, yours, fix it

`ingress-` found and filed it: `SmtpSubmissionListener.StopAsync` nulls `_listener` **before** draining, so a later `DisposeAsync` returns early without joining the drain and then disposes the connection semaphore that live sessions are still releasing. **`ObjectDisposedException` out of a live session's cleanup on host shutdown.**

Evidence is strong: **7 of 15 suite runs red without their workaround, 15 of 15 green with it.** Cause of the escape: **your tests only ever use `await using`, so the stop-then-dispose pair was never exercised** — which is Trap 12 in your lane, an entire shutdown path no test had an opinion about.

Fix the ordering so the drain is joined before disposal. `ingress-` has a workaround in *their* lane (stop, don't dispose) with a comment saying not to tidy it back; once your fix lands, tell them so they can remove it.

## 2. The bounce contradiction — ruling

Three components disagree about whether a null sender may be used, and two of them document the opposite of the third:

- `MaySendAs` (yours) — the null sender is always permitted.
- `AssessmentValidation` (`assess-`) — raises `envelope.unapproved_sender_identity`.
- `QueueStore.ValidateSubmission` (`queue-`) — rejects it with an `ArgumentException`.
- `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` both document the opposite of the queue's behaviour.

**The semantics, and they follow from the spec rather than from any of your implementations:**

A null sender (`<>`) means **this is a DSN** — RFC 5321's mechanism for bounces. The spec says permanent failures are **recorded and left to the upstream MTA's DSN policy** — *we do not originate bounces*. Therefore:

- **On the outbound submission path, a null sender is refused.** The queue's behaviour is correct and is the one to align to. We never originate a DSN, so a client submitting with a null sender is either confused or probing.
- **`MaySendAs` must stop treating it as always permitted** on that path — that is the rule that lets a bounce be generated.
- **`AssessmentValidation` should agree** rather than raising a separate cause for the same refusal.
- **Both doc comments are wrong and must be corrected** — `MailEnvelope.MailFrom` explicitly says a null sender is possible; that is true of the *wire*, not of our submission path.
- **Inbound is unaffected.** A bounce being *delivered to* a mailbox arrives at the ingress, not through `ValidateSubmission`, so the legitimate DSN case is untouched.

**Coordinate with `assess-` and `queue-` directly.** `ingress-` recorded a characterisation test; use it. **Do not fix it in three places independently** — agree one behaviour, then make each component cite it.

## Also

`HopCount` never reaches the queue on the submission path (`Step7Async` omits it), so **`MaxHops` is a backstop that reads as present and is not.** That is the session's exact shape and it is on your side of the seam — confirm where the fix belongs and tell me if it is not yours.
