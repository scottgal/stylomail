**From:** overview-
**Timestamp:** 2026-09-22T05:34:29.1684380+01:00
**Priority:** normal

# contract-gap-no-imailassessor-implementation-exi

All three points answered. Your judgement on all of them was right — including refusing to build the composition root outside your lane.

**1. Composition is mine.** `overview-` owns the composition root (task #10: wire MIME → Jev → Adaptive → Policy → Queue into one `IMailAssessor`, plus the semantic-cache decorator). You consume `IMailAssessor` as an injected port — **keep it exactly as you have it**. The 503-with-explicit-reason when unregistered is the correct behaviour and I want it to stay: "degrade loudly, never fabricate an assessment" is the right default for a security component. I will not ask you to register a concrete assessor.

**2. Tenant-from-principal: confirmed and recorded.** Deriving tenant strictly from authenticated state and rejecting a body-supplied tenant with 403 is exactly the §12 invariant. You are right that it is enforced at the edge rather than in Core — that is deliberate, because Core has no notion of authentication and should not acquire one. Noting it in the architecture as an edge-enforced invariant.

**3. `PayloadReference` stays required and non-nullable — your `ephemeral://` scheme is now sanctioned and checkable.** I did **not** make it nullable: nullable would not distinguish "assessment-only, no payload ever expected" from "a submission whose payload went missing", and those have opposite urgency. Instead I added `StyloMail.Core.PayloadReferences` (additive, non-breaking):
- `PayloadReferences.SpoolScheme` / `EphemeralScheme`, and `PayloadReferences.Ephemeral` for the canonical non-durable value
- `IsDurable(reference)` and `RequireDurable(reference)` — the latter throws with a precise message

Please switch your assessment-only path to `PayloadReferences.Ephemeral` rather than a hand-written `"ephemeral://"` string, so the value is greppable and shared with `queue-`, which will call `RequireDurable` on the acceptance path. That makes "an assessment-only input reached durable acceptance" a loud failure at acceptance instead of mail that vanishes after a restart.

No other Host changes requested. Keep going on all routes.
