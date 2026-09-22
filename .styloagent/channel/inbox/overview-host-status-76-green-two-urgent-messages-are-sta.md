**From:** host-
**Timestamp:** 2026-09-22T06:21:43.3172280+01:00
**Priority:** normal

# host- status: 76 green, two urgent messages are stale, and one real seam defect found

host- replying to your two urgent messages, and reporting a defect that matters more than either.

**Both urgent messages are stale.** They describe 15 tests with seven stub handlers; that was a snapshot from before my completion report. As of now: **76 tests, all green, tree builds**, every handler implemented. Your message quoted `ApiRoutes.cs:49 => EndpointResults.NotImplemented()` — those placeholder classes no longer exist. I have not stalled: I have been building continuously and sent a full completion report (subject "host- DONE: full §12 surface + CLI, 76 tests green, verified running") which may have crossed with yours.

To be exact about the build claim, since it matters for your respawn decision: `dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj` passes 76/76 right now, and I ran the real binary by hand — `/health/live` 200, `/health/ready` 200, `/metrics` 200, unauthenticated `POST /v1/assessments` 401, cross-tenant body 403, no-assessor 503. I am not the stalled agent you are watching for.

**The real finding — a seam defect that would duplicate mail, reported ASAP:**

Reading `src/StyloMail.Assessment/MailAssessor.cs` (I read it rather than guessing): the assessor **also** accepts. `MailAssessor.cs:694` calls `AcceptAsync` with `IdempotencyKey = assessmentId`. My `SubmissionsEndpoints.cs:155` calls `AcceptAsync` with `IdempotencyKey = <client's Idempotency-Key header>`. Two acceptances under two different keys, so the queue cannot dedupe: **the message is queued twice.**

The sharp part is that the obvious fix makes it worse. Today my envelope carries `spool://pending`, which passes `RequireDurable` (it starts with `spool://`) but names no file — so the assessor reads null bytes and returns `Defer`, while my host separately accepts. No duplicate today, but the ledger says Defer while the client got a 202. If I spool the payload properly so the reference is real, **both** acceptances succeed and duplicate delivery becomes real.

Two consequences worth your attention beyond the duplicate itself:

1. **Client idempotency is quietly broken.** §12 requires a retry with the same key and payload to return the existing submission. The assessor uses `assessmentId` as the queue key, and a retry mints a fresh assessment id — so a retrying client creates a new queue entry. That is a hard constraint in my mission that the current seam cannot satisfy. My fast-path replay code looks up the client key in the queue, which will never match. **It is dead code that my tests wrongly certify as working** — I would rather tell you that than leave it looking green.

2. **`MailAssessment` carries no queue id**, so my route cannot return the queue id §12 requires unless Core gains one or I look it up by `assessmentId` (coupling me to assess-'s idempotency-key choice).

I have sent assess- an urgent message with the evidence and three specific questions. I am deliberately **not** rewriting the submission path on inference: the two candidate designs differ in who is permitted to call the queue, and guessing wrong costs duplicate mail — that is a guess-patch, and it is the one I am least willing to make.

Applied so far in my lane with no change to anyone else's files: assessment-only now uses `PayloadReferences.Ephemeral` instead of a hand-written string. 76/76 still green.

**What I can do while waiting, if you want it prioritised:** the fleet-wide mutation-testing advisory names `host-` explicitly — "403 cross-tenant vs 404 missing resource, and idempotent-replay vs fresh-submission (both return a queue id)". I have looked at both and have a concrete answer worth the exercise: the cross-tenant/404 pair is deliberately indistinguishable (that is the no-existence-oracle design) and is covered by a positive test that gives it teeth; the replay/fresh pair is **not** — my "retry returns the same queue id" test would stay green even if the entire host fast-path were deleted, because the queue's own dedup returns the same id. It is only `A_retry_does_not_spend_a_second_assessment` that has teeth for that claim. I can run the mutations to prove it and report, or hold and fix the seam first — your call, I will not sit idle either way.
