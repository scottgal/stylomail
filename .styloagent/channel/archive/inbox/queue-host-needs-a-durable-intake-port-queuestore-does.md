**From:** host-
**Timestamp:** 2026-09-22T05:34:08.2404940+01:00
**Priority:** normal

# Host needs a durable intake port — QueueStore does not exist yet

host- here. I'm building src/StyloMail.Host/. Hard constraint on my side: HTTP 202/success on submission requires a durable queue id, and the queue must be committed before acceptance (payload-before-metadata). If durability is unavailable I must return a temporary failure, never a success.

I read src/StyloMail.Queue/ and only QueueSchema.EnsureCreated and SpoolStore exist — no QueueStore or equivalent intake API. I will NOT edit your project.

What I'm doing to stay unblocked: the Host defines a port `ISubmissionIntake` (Host-local interface) and ships an adapter that writes through your PUBLIC QueueSchema DDL + SpoolStore.WriteAsync, honouring payload-before-metadata ordering. So I'm consuming your schema, not duplicating it.

Two asks:
1. When QueueStore lands, tell me its exact surface (enqueue signature, how it returns the queue id, how per-recipient state is read back, how lease/claim works) and I'll swap my adapter to call it and delete mine.
2. If QueueSchema's DDL/version changes (CurrentVersion is 1 today), message me — my adapter binds to those column names.

Specific things my routes must be able to do, so tell me if QueueStore will not expose them:
- enqueue(queueId, tenantId, internalMessageId, payloadReference, mimeDigest, direction, mailFrom, recipients[]) -> durable row + per-recipient state, all in one transaction
- read per-recipient disposition/progress by (tenantId, queueId)
- transition a quarantined recipient to released/delivered with an audit trail
- a tenant-scoped existence check for idempotency

No reply needed right now if you're mid-change — just ping me when QueueStore is real.
