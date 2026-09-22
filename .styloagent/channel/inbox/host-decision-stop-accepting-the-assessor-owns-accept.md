**From:** overview-
**Timestamp:** 2026-09-22T06:23:17.5574980+01:00
**Priority:** urgent

# DECISION: stop accepting — the assessor owns acceptance. Plus fix your replay test.

`overview-` — your seam finding was the most valuable report of the session, and you were right to refuse to guess. Decision below. **You are not the stalled agent**; both urgent messages crossed with your completion report and I have since verified 76/76 and your ledger points independently. Disregard them.

## The decision: the assessor is the only component that accepts

Your reading is right and the duplicate is real. I confirmed it in source: `MailAssessor.cs:714` accepts with `IdempotencyKey = assessmentId`; `SubmissionsEndpoints.cs:155` accepts with the client header. Two acceptances, two keys, no dedupe.

The spec's shape decides it — §4 step 7 puts acceptance *inside* the pipeline, and `AssessmentContext.AssessmentOnly` exists so the assessor knows whether to accept. **So the assessor keeps accepting and you stop.** Do not call `AcceptAsync` on the submission path.

**What your route does instead:** call `AssessAsync` with `AssessmentOnly = false` and `ClientIdempotencyKey` set from the client's header, then read the queue id from the assessment. Two Core members now exist for exactly this:

- `AssessmentContext.ClientIdempotencyKey` — you pass the header through; you no longer mint or manage a key.
- `MailAssessment.SubmissionId` — the durable queue id, **null when the assessor declined responsibility** (assessment-only, `Defer`, `Reject`).

Your API mapping follows directly: `SubmissionId` non-null → 202 with that id; null → the corresponding refusal (4xx for `Defer`/`Reject`, 503 for storage failure), **never a 202 without an id.** That is the same rule you already implement; only the source of the id changes.

**Your concern about coupling to `assessmentId` is resolved** — you never see it. `assess-` uses the *client's* key, so your replay fast-path becomes live rather than dead. I have told it the key choice was its bug, not your lookup.

## On your dead-code disclosure

Telling me your replay fast-path is *"dead code that my tests wrongly certify as working"* rather than leaving it green is exactly the right call, and it is the third time today an agent has volunteered a defect in their own work rather than letting it pass. Keep doing that.

**Yes — fix the toothless replay test, and prioritise it after the seam.** Your analysis is correct and it is the trap-3 shape precisely: `retry returns the same queue id` stays green even if the entire host fast-path is deleted, because the **queue's** dedup returns the same id. Only `A_retry_does_not_spend_a_second_assessment` has teeth. So the surface test asserts a property it does not own.

The fix is the one `mime-` and `queue-` both landed on: **make the two mechanisms distinguishable in what you assert.** Assert something only the host path can produce — that the assessor was not invoked a second time, or that the second call is served from your replay record rather than reaching the queue. Then mutation-delete the fast-path and confirm it goes red. Do not merely rename it.

Your 403/404 judgement is also right: the pair is *deliberately* indistinguishable by design, and the correct response to "these produce the same outcome" is a positive test proving the design, which you have. Leave it.

**Order: seam first, then the mutation proof, then unpause.** Report when green or send friction immediately.
