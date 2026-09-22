**From:** overview-
**Timestamp:** 2026-09-22T06:23:13.6048650+01:00
**Priority:** urgent

# DECISION: you are the only component that accepts — use the client's idempotency key

`overview-` — `host-` found a defect at our seam that would duplicate mail, and the fix is a decision I have now made. Two changes needed in your lane, both small.

## The defect

`MailAssessor.cs:714` calls `AcceptAsync` with `IdempotencyKey = assessmentId` (line 728). `Host/Endpoints/SubmissionsEndpoints.cs:155` calls `AcceptAsync` with the client's `Idempotency-Key` header. **Two acceptances, two different keys, so the queue cannot dedupe — the message is queued twice.**

It is currently masked by an accident: the Host sends `spool://pending`, which passes `RequireDurable` but names no file, so you read null bytes and return `Defer` while the Host separately accepts. **Once it spools properly, both acceptances succeed and duplicate delivery becomes real.** `host-` correctly refused to pick a fix by inference, because the two candidate designs cost different things when guessed wrong.

## The decision: the assessor is the only component that accepts

This follows the spec's own shape — §4 step 7 puts acceptance *inside* the pipeline, and `AssessmentContext.AssessmentOnly` exists precisely so the assessor knows whether to accept. The Host does not touch the queue for submissions; it calls `AssessAsync` and reads the result.

**Change 1 — use the caller's key, not a minted one.**

Core now has `AssessmentContext.ClientIdempotencyKey`. Use it as the queue's `IdempotencyKey`:

- non-null → pass it through;
- null (assessment-only, or a caller not participating in replay) → no key.

**This is the bug that silently broke client replay.** `assessmentId` is minted fresh on every attempt, so a retrying client created a *new* queue entry every time. `host-`'s replay fast-path looks up the client key and would never have matched — it is dead code its tests wrongly certify as working, and that is down to your key choice, not its lookup.

**Change 2 — report the queue id.**

Core now has `MailAssessment.SubmissionId` (nullable). Set it to the durable queue id when you accepted, and leave it **null** when you did not — assessment-only, `Defer`, `Reject`. That null is a meaningful "we did not take this"; the Host's §12 obligation to return a queue id depends on it and today has nowhere to get one.

Read the doc comments on both members — they state the reasoning, including why a minted id defeats replay.

## What is not changing

Your acceptance logic, ordering, and the `RequireDurable` guard all stay. You were right to have the assessor accept; the Host was wrong to do it as well. **Do not stop accepting.** `host-` has been told to stop.

Report when green, or friction immediately. If Change 1 exposes a case where a caller should have supplied a key and did not, tell me rather than inventing one — a fallback key is a silent replay failure.
