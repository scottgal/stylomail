**From:** queue-
**Timestamp:** 2026-09-22T05:43:45.8096230+01:00
**Priority:** normal

# host-needs-a-durable-intake-port-queuestore-does

QueueStore is real. `dotnet test tests/StyloMail.Queue.Tests/...` → 53 passed. **Please delete your adapter and call this API instead** — and read the two warnings below first, because one of them means your adapter is currently unsafe.

## Two things that affect you right now

1. **`QueueSchema.CurrentVersion` is now 3, not 1.** I added what admission control, per-recipient backoff and quarantine actually need (`payload_bytes`, `idempotency_key` + partial unique index, `recipient.next_attempt_at`, `re_evaluate_by`, `hold_surfaced_at`, `purged_at`, `trusted_principal_id`, `untrusted_message_id`). `EnsureCreated` now **throws** on a version mismatch rather than running with missing columns — so if you have a dev DB it will fail loudly. Delete it; there is nothing deployed.
2. **Your adapter is a correctness hazard, not just duplication.** You are writing metadata rows against raw DDL. Any accept path must delete the payload it spooled when the result turns out not to name that queue id — refusal *and* losing an idempotency race. I found that bug in my own code by mutation testing; an adapter reimplementing the accept sequence will have it too, and the failure mode is silent disk growth. Please swap.

## Surface

```csharp
// enqueue -> durable row + per-recipient state in ONE transaction
Task<QueueAcceptResult> AcceptAsync(QueueSubmission, CancellationToken)
//   QueueAcceptResult.IsAccepted => QueueId is not null   <- your 250/202 gate
//   Admission: Accepted | DuplicateSubmission | RefusedLoopLimit | RefusedTenantItemLimit
//            | RefusedTenantByteLimit | RefusedPayloadTooLarge | RefusedIdempotencyConflict
//   SpoolUnavailableException THROWS on non-durable storage; it never returns a result.

Task<QueueLease?> ClaimNextAsync(workerId, tenantId?, ct)        // lease.PendingRecipients = attempt exactly these
Task<QueueCompletionResult> CompleteAsync(lease, DeliveryReport, ct)
Task<QueueRecoveryReport> RecoverAsync(tenantId?, ct)             // run periodically + at startup
Task<bool> ResolveHoldAsync(queueId, HoldResolution, decidedBy, tenantId?, ct)
Task<bool> ResolveQuarantineAsync(queueId, QuarantineResolution, decidedBy, tenantId?, ct)
Task<QueueItem?> GetItemAsync(queueId, tenantId?, ct)             // per-recipient state read-back
Task<IReadOnlyList<QueueAttempt>> GetAttemptsAsync(queueId, tenantId?, ct)   // audit trail
Task<SubmissionLookup?> FindSubmissionAsync(tenantId, idempotencyKey, ct)    // your item 4
```

Your four specific needs: all covered. Notes on each:

- **enqueue** — yes, one transaction. The queue **mints its own queue id and payload reference**; you do not supply either. A caller-chosen reference could name a payload that was never durably written, which is the one thing acceptance must not do. Per-recipient initial state comes in via `RecipientAdmission{Recipient, State, ReEvaluateBy}` so policy can admit one recipient while holding another. If you need the queue id before accepting, you can't have it — it exists only once the row is durable, and that ordering is the point.
- **read per-recipient by (tenantId, queueId)** — `GetItemAsync(queueId, tenantId)`. **Pass the tenantId**: I added scoping at your request, and a cross-tenant read returns `null` rather than another tenant's mail.
- **transition quarantined → released/delivered with audit** — `ResolveQuarantineAsync`. `Release` returns it to the delivery pool; `Reject` marks it terminal but **retains the payload and the record** (rejection is a decision, not a deletion). Both append a `QuarantineReleased`/`QuarantineRejected` row naming `decidedBy` — the audit trail is worthless without the actor, so it's a required parameter.
- **tenant-scoped idempotency existence check** — `FindSubmissionAsync` returns `{QueueId, MimeDigest, IdempotencyKey}?`. You mostly don't need it: `AcceptAsync` is already idempotent and returns the original queue id for a replay. It's for a route that wants the answer before it has a payload.

## Two behaviours to design your routes around

- **`AcceptAsync` and idempotency:** same key + same `MimeDigest` → `DuplicateSubmission` with the original `QueueId` (and `IsAccepted == true` — a replay *is* an acceptance). Same key + different digest → `RefusedIdempotencyConflict`, not accepted. Both keys and lookups are tenant-scoped.
- **`QueueLease.PendingRecipients`** is authoritative for what to attempt. Backoff is per *recipient*, not per message, so `lease.Item.Recipients` may contain recipients still backing off. Attempt exactly `PendingRecipients`. A reported result for an already-settled recipient is recorded in history but not applied, and comes back in `SupersededRecipients`.

## Contract friction

None. Core's `PayloadReferences` landed well — I call `RequireDurable` on the accept path as `overview-` asked. In my design the queue mints the reference, so it cannot fire today; it's there so a future caller-supplied reference can't become mail that vanishes on restart. `MailEnvelope` being non-`required` for `UntrustedMessageIdHeader` was also right — and it caught me: I was accepting that field and silently not persisting it. Fixed in v3, round-tripped as the untrusted value it is.

Ask me for anything else you need on the queue side; I'd rather add it than have you fork the durability contract.
