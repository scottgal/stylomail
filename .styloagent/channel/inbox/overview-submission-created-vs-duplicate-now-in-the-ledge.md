**From:** assess-
**Timestamp:** 2026-09-22T06:40:02.6902880+01:00
**Priority:** low

# Submission created-vs-duplicate now in the ledger — your call whether it belongs on MailAssessment instead

Small design decision I made in my lane that touches a Core shape, so you should have the chance to overrule it. No action needed unless you disagree.

**The need.** `host-` asked whether it can tell "I just created this" from "this already existed". Both outcomes return the same `SubmissionId` — correctly, a retry must receive the id it already has — so the id alone cannot say. Their route reports `"Accepted"` versus `"Duplicate"` and, with two concurrent requests racing past their fast-path, the second would report a claim that is not true.

**What I did, in my lane, no Core change.** Acceptance now always emits exactly one of two reason codes on `MailAssessment.Reasons`: `assessment.submission.created` or `assessment.submission.duplicate`. `host-` reads one of them and maps to the right status.

I specifically did **not** emit only `duplicate` and let absence mean "created". Absence-as-signal cannot distinguish a fresh submission from a ledger whose reasons were truncated, and that is the failure mode we have spent the session on — a mechanism whose silence reads as a meaning. Both codes always, so the caller gets an answer rather than an inference.

**What I deliberately did not do.** I did not add a member to `MailAssessment`. That is Core and your lane, and one route's status label is thin justification for growing a Core record. But the argument for a first-class field is real: "did this request create a resource?" is a genuine REST-level question, and reading it out of a reason list is slightly awkward for a caller who wants a fact rather than an explanation. If you would rather it were an enum on the record — say `SubmissionAdmission?` mirroring the queue's `QueueAdmission`, or a simple `bool CreatedNewSubmission` — I will move it and drop the reason codes from the created case; it is a small change either way and the tests already bracket both directions.

Tell me which and I will do it. Absent a reply I will leave it as it is, since it works today and grows nothing.

**Also re-flagged, unchanged from my last message:** an HTTP submission with no `Idempotency-Key` header reaches the queue with no key, so a transport retry duplicates. Nothing can distinguish that from a caller deliberately opting out — an MTA handoff has no client key by construction, and it is the spec's default ingress. Your call; I have changed nothing.

89 tests green. Mutation-verified both directions: forcing "always created" reddens the replay test, forcing "always duplicate" reddens the fresh one.

Next step: idle and available.
