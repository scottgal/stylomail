**From:** host-
**Timestamp:** 2026-09-22T06:05:05.3097870+01:00
**Priority:** normal

# host- progress: assess + submit + decisions landed, 42 tests green

host- status report. Action: built the first slice of src/StyloMail.Host/. Result: 42 tests green, no live Jev or network in the suite.

**Routes implemented so far** (`src/StyloMail.Host/Endpoints/`):
- POST /v1/assessments — Assess privilege. Full assessment returned; AssessmentOnly=true so it cannot send or train.
- POST /v1/submissions — Send privilege. 202 + durable queue id, Idempotency-Key header honoured.
- GET /v1/submissions/{id} — Send privilege. Per-recipient progress from queue state.
- GET /v1/decisions/{id} — Review privilege. Evidence/reasons/versions/coverage from the ledger.

**Still to come:** feedback, quarantine release, sender pause, health/metrics, CLI, CSRF cookie channel.

**Verified security properties (each has a test):** unauthenticated and unknown-key requests 401 on every /v1 route and never reach the assessor; tenant is taken from the principal and a conflicting body tenantId is 403 and never reaches the assessor; a sender cannot select shadow mode; cross-tenant decision read is 404 with a body byte-identical to a nonexistent id (no existence oracle); idempotent replay returns the same queue id and does not re-assess; same key + different payload is 409; storage unavailable is 503, never 202; assessment creates zero queue rows.

**Contract friction worth your attention:**

1. **`IMailAssessor` still has no implementation.** Host registers `UnavailableMailAssessor`, which throws and maps to 503 `assessor_unavailable`. Tests inject a fake. The host is therefore fully testable but **not runnable end-to-end** — it will refuse every real message until someone owns the composition (Mime → Jev → Adaptive → Policy → MailAssessment). Please decide the owner; I will not reach into those lanes.

2. **`MailEnvelope.PayloadReference` is `required` but assessment-only calls have no durable payload.** I use `ephemeral://assessment/{id}` rather than claiming a spool reference that was never written. If you would rather it were nullable for the assessment path, that is a Core change and yours.

3. **queue- shipped `QueueStore` mid-flight** (QueueSchema is now v3 and `EnsureCreated` takes a TimeProvider). I bind `ISubmissionIntake` directly to Queue's own public contracts (`QueueSubmission`, `QueueAcceptResult`, `QueueItem`, `QuarantineResolution`) rather than duplicating them — tenant-scoped idempotency and payload-before-metadata ordering are theirs and I consume them. No duplication, but it does mean the Host tracks their contract changes.

**Design decisions you may want to overrule:**
- Reading the decision ledger requires the **Review** privilege. A sender cannot read the ledger entry for their own message. §12 says "reviewer reads the ledger", so I read that as review-only. Say the word if senders should see their own.
- GET /v1/decisions/{id} and /v1/submissions/{id} return **404** for another tenant's resource rather than 403, deliberately, to avoid an existence oracle over ids.
- Shadow mode requires **Administer**. A sender requesting it gets 403 rather than being silently ignored.

Next step: privileges + CSRF + quarantine release, then health/metrics, then the CLI.
