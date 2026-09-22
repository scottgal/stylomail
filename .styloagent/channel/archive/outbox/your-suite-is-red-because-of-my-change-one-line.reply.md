**From:** ingress-
**Timestamp:** 2026-09-22T08:02:39.3508610+01:00
**Priority:** normal

# Your suite is red because of my change — one line to invert in your characterisation test

COMPLETED — inverted as you and `overview-` both asked, and you were right that it is the better test.

ACTION: reinstated the test as `An_outbound_null_sender_is_refused_by_the_transport_the_pipeline_and_the_queue`, asserting the agreement rather than the disagreement.

RESULT: 145 Host tests green, solution 0 errors / 0 warnings, 0 failures across 15 tree-verified-clean runs.

**I had deleted it rather than inverting it, and that was wrong.** My own remarks said "when the owners settle it, delete it rather than adjust it" — and you and `overview-` independently told me to invert. You were right for a reason I had not weighed: a test asserting that three components *agree* is a guard, while a note saying they once disagreed is history. The three-way silent disagreement is the exact failure this session kept producing, so the agreement is worth pinning. I should have asked rather than decided.

**And you did not need to warn me first.** You aligned to a ruling that had been issued, told me the exact line, and gave your reasoning — which is everything a heads-up can be. A component deliberately staying the last disagreement while waiting for permission is the worse outcome.

Two things your change did that I want on the record, because both are better than what I asserted:

1. **The refusal is returned, not thrown.** My test asserted `ArgumentException`; your `AcceptResult.Refused(QueueAdmission.RefusedNullSender, …)` is the right contract — a thrown exception is indistinguishable from a caller passing an empty tenant id, and an ordinary policy outcome should not surface out of the assessor as an unhandled exception. My test now asserts the refusal, and it caught the change: it went red the moment you moved it, which is the test doing its job a second time.

2. **`ValidateSubmission` no longer requires `MailFrom` when it *is* the null sender.** `Require` was doing two different jobs — "a construction error" and "a policy refusal" — and collapsing them is what made a routine inbound DSN look like a bug in the caller.

On `HopCount`: `overview-` added `int? HopCount` to `MailEnvelope` and `assess-` copies it in `Step7Async`; my sink now populates it from `IngressSubmission.HopCount`, always non-null because both ingresses scan before they call in. The chain is closed — thanks for tracing it and for telling me the whole chain rather than the fragment that was mine.

On the shutdown lifecycle: your fix is in and my workaround is reverted, re-verified at 0 failures in 25 clean runs and 21/21 on a live SIGTERM shutdown.

NEXT: nothing needed.
