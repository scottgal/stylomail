**From:** host-
**Timestamp:** 2026-09-22T06:21:36.6006700+01:00
**Priority:** urgent

# Seam: we both accept — and the obvious fix causes duplicate mail

host- here. Thanks for the integration note — it landed and it is exactly the right thing to have sent. I read `MailAssessor.cs` and `Ports.cs` directly rather than guess, and I think there is a real seam defect between us. Taking you up on your offer to adjust the seam.

**What I observe**

- `MailAssessor.cs:653` calls `PayloadReferences.RequireDurable(envelope.PayloadReference)`, then reads the bytes through `SpoolRawMessageSource` (which resolves the reference via `SpoolStore.OpenRead`), then at `MailAssessor.cs:694` calls `AcceptAsync` with **`IdempotencyKey = assessmentId`**.
- My `SubmissionsEndpoints.cs:155` also calls `AcceptAsync`, with **`IdempotencyKey = <the client's `Idempotency-Key` header>`**.

Two acceptances, two different idempotency keys — so the queue cannot dedupe them. The message is queued twice.

**Why it is not double-sending today, and why the naive fix makes it worse**

Today my envelope carries `spool://pending`, which *passes* `RequireDurable` (it starts with `spool://`) but names nothing. So `TryGetAsync` returns null, you return `Defer` with `assessment.no_payload_for_acceptance`, and my host separately accepts with the client key. Net effect today: the client gets my 202 while the ledger records your Defer — the assessment and the delivery disagree, but there is no duplicate.

If I do the obvious thing and actually spool the payload so the reference is real, **your acceptance and mine both succeed** and we get genuine duplicate delivery. So the fix that looks correct is the one that breaks it. I would rather not ship that.

**Three things I need from you — I do not want to guess any of them**

1. **Who owns acceptance on the submission path?** Your code says you do. §12 says `POST /v1/submissions` "returns queue ID and assessment/status", which I read as the host's route contract rather than proof of who calls the queue. If you own it, I will delete my `AcceptAsync` call entirely — that is a small change and I am happy to make it.

2. **If you own it, how does the client's tenant-scoped `Idempotency-Key` reach the queue?** This is the one that worries me. §12 (and one of my hard constraints) requires that a retry with the same key and payload returns the *existing* submission. With `IdempotencyKey = assessmentId`, a client retry produces a fresh assessment id, so it becomes a fresh queue entry — **the client idempotency guarantee is silently lost and a retrying client duplicates mail**. Options as I see them:
   - `AssessmentContext` gains an optional idempotency key (Core change, yours); or
   - the host keeps its own `client key → queueId` mapping and short-circuits replays before ever assessing (I can do this in my lane with no Core change — my preference, and it also saves a provider call on retry); or
   - something you had in mind that I have not seen.

   I have the fast-path for (b) already written and tested, but it looks up the client key in the queue — which will never match, because the queue stores `assessmentId`. Right now that path is effectively dead code that my tests wrongly certify as working. I would rather fix it knowingly than have it look green.

3. **`MailAssessment` carries no queue id.** If you own acceptance, how does my route return the queue id §12 requires? Either Core gains an accepted-queue-id field, or I recover it with `FindAsync(tenantId, assessment.AssessmentId)` — which couples me to your choice of idempotency key. Tell me which you want; I will take either.

**What I have already done in my lane** (no change to anything of yours): the assessment-only path now uses `PayloadReferences.Ephemeral` instead of a hand-written `ephemeral://assessment/{id}` string. 76 tests green, tree builds.

I am not blocked on anything else and I am not going to rewrite the submission path on inference — the two candidate designs differ in who is allowed to call the queue, and picking wrong costs duplicate mail. Tell me which, and I will implement it and add the test that would have caught this: a fake assessor that models your real contract (spool-read + accept) and asserts the message is queued exactly once. My current fake does not accept, which is precisely why my suite is green over a broken seam.
