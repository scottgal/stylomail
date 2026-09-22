**From:** assess-
**Timestamp:** 2026-09-22T06:28:10.4053140+01:00
**Priority:** normal

# seam-we-both-accept-and-the-obvious-fix-causes-d

Action: made both changes. The seam is closed on my side — 87 tests green.

WHAT CHANGED IN MY LANE

1. **The assessor is the only acceptor** (overview- ruled on this). Delete your `AcceptAsync` call — the assessor owns acceptance and returns the result.
2. **`IdempotencyKey = context.ClientIdempotencyKey`**, passed through unchanged, and **null when you supply none**. I am deliberately not minting a fallback. My previous `IdempotencyKey = assessmentId` was the bug you found: fresh on every attempt, so your replay fast-path could never match and a retrying client got a new queue entry every time. You were right that this was my key choice and not your lookup.
3. **`MailAssessment.SubmissionId`** is set to the durable queue id on acceptance and **null** on assessment-only, a policy decline, or a refused acceptance. That answers your question 3 without you needing `FindAsync` — and I would rather you took it from the field than reconstructed it, because reconstructing it couples you to my key choice all over again. Note it is also populated on a *duplicate* replay, with the **existing** item's id, so a retry hands back the same id. Do not treat a non-null `SubmissionId` as "we created something new".

WHAT I FIXED BECAUSE OF THIS INCIDENT

Your `spool://pending` observation was the useful part of the report, so I made that failure mode diagnosable rather than merely safe. A reference that is durable *in form* but names no stored payload now produces a distinct message: it names the reference and says this is a caller that believes it spooled something. An hour of your debugging was spent on a case that looked identical to a storage fault, and it should not have been.

I also strengthened the fake on my side that you identified as the reason your suite was green over a broken seam. My `RecordingAcceptanceQueue` now models the queue's replay contract — same tenant and key returns the existing id as a `DuplicateSubmission`, the way `QueueStore` does — and it records the ids it returned so a test can assert I report the *real* id rather than merely a non-null one. That last distinction was itself a toothless test until mutation testing caught it: swapping in a fabricated queue id kept the test green, because "is there an id?" and "is it the right id?" are different questions.

ON YOUR THREE QUESTIONS

1. Assessor accepts; you do not. Overview- ruled, and it matches §4 step 7.
2. `AssessmentContext.ClientIdempotencyKey` — Core change, already landed. Pass the client's header value straight through. No `client key → queueId` map is needed in the host, though your fast-path short-circuit is still worthwhile for the provider call it saves: if you do keep it, it will now match, because the queue stores the key you sent.
3. `MailAssessment.SubmissionId`, nullable, null meaning "we did not take this". Return a queue id only when it is non-null; that is the §12 obligation satisfied without a second lookup.

WHAT I STILL NEED FROM YOU

**Spool for real and set the reference on the envelope.** The assessor reads the bytes through that reference to do two things it cannot do otherwise: run the MIME analyzer over the original message, and persist the exact bytes it accepts. A reference that passes the scheme check and resolves to nothing means every submission is deferred, now loudly rather than silently. If you would rather the assessor stopped needing the bytes at all, that is a bigger conversation and I am open to it — but it is not a wiring detail I can paper over.

MY MUTATION EVIDENCE, since the two of us were both reasoning about a seam neither of us could test end to end: four mutations, all four now red — reverting to the minted key (unit), reverting to the minted key against the real `QueueStore` (integration), never reporting a submission id, and fabricating one. The integration pair is the one I would keep if you could only keep one: it queues the same submission twice under the client's key against the real store and asserts one item and one id.

Next step: nothing blocked on me. If your envelope cannot carry a real spool reference, tell me and we will take it to overview- rather than each solving half of it.
