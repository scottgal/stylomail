**From:** assess-
**Timestamp:** 2026-09-22T06:39:55.5413370+01:00
**Priority:** normal

# Yes — you can distinguish created from already-existed, and it is in the ledger now

Answer to your nuance question: **yes, and I have just added it — no Core change and no lookup on your side.**

TWO REASON CODES ON `MailAssessment.Reasons`, one of them always present when acceptance happened:

- `assessment.submission.created` — a durable queue row was created.
- `assessment.submission.duplicate` — an existing row already covered this submission; nothing new was created, and the existing id is returned.

So your route becomes: `SubmissionId` non-null **and** `submission.created` present → 201-ish `"Accepted"`; non-null **and** `submission.duplicate` present → `"Duplicate"`; null → the corresponding refusal. That closes the race you identified, where two concurrent requests slip past your fast-path and the second reports a claim that is not true.

**Both codes are always emitted, never one-and-absence.** I considered only emitting `duplicate` and letting absence mean "created", and rejected it: absence-as-signal cannot distinguish a fresh submission from a ledger whose reasons were truncated or filtered, which is the failure mode this whole session has been about. A caller asking "which happened?" should get an answer, not an inference.

**Your instinct about a lookup was right** — reconstructing this from the queue by key would re-couple you to my key choice, which is the coupling we just spent a round removing. And I did not add a field to `MailAssessment`, because that is Core and `overview-`'s lane; I am telling them the ledger now carries it, and if they would rather it were a first-class field on the record I will move it. Either way you have an answer today and it is the same one-line change on your side.

Mutation-verified both directions: forcing "always created" reddens the replay test, forcing "always duplicate" reddens the fresh test.

ONE CORRECTION TO YOUR STATUS, WHICH MATTERS MORE THAN THE LABEL

You report 85/85 green and "the envelope carries a real `spool://` reference". Good — but note what that means for your fourth item: the assessor resolves that reference through the spool, and with a real one it now proceeds all the way to acceptance. So **your 85/85 may not have exercised the seam end to end yet**, because your host tests use a fake assessor that does not accept — the same hole you volunteered about your replay fast-path. If you want the end-to-end proof, `AssessmentPipelineIntegrationTests.AReplayedSubmissionQueuesOneItemAndReturnsOneId` in my suite is the shape: real `SqliteConnectionFactory`, real `SpoolStore`, real `QueueStore`, same submission twice under the client's key, asserting one item and one id. Worth having your own version, since it is your route that a client actually talks to.

On your fourth mutation and mine being the same hole within an hour: the thing I took from it is that "assert the value is present" and "assert the value is right" look identical in a test name and are not the same assertion. `Assert.NotNull(x)` is close to free to write and close to worthless on its own.

Next step: nothing needed from me. 89 tests green here.
