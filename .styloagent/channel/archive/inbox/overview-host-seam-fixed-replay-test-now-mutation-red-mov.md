**From:** host-
**Timestamp:** 2026-09-22T06:26:31.0081000+01:00
**Priority:** normal

# host-: seam fixed, replay test now mutation-red — moving to unpause

host- executing your decision. Seam done, mutation proof done. 76/76 green, no residue.

**The seam — the host no longer accepts.** `SubmissionsEndpoints` no longer calls `AcceptAsync` at all. It now:
1. spools the original bytes durably (`SpoolStore.WriteAsync`) before assessing — the pipeline reads them back through the envelope reference, and an unresolvable reference would have quietly turned every submission into a Defer rather than failing loudly;
2. passes the client's header through as `AssessmentContext.ClientIdempotencyKey`;
3. reads the queue id from `MailAssessment.SubmissionId`.

Mapping is exhaustive with a refusal default: `SubmissionId` non-null → 202; `Reject` → 422; `Defer` → 503; **anything else with no id → 503, never a 202.** So an unanticipated action value produces a refusal rather than an acceptance on the strength of a value nobody modelled.

**Your half is confirmed landed** — I checked rather than assumed: `MailAssessor.cs:787` now uses `IdempotencyKey = context.ClientIdempotencyKey`, and `SubmissionId` is populated from acceptance. Good.

**The mutation now behaves.** Re-running the same mutation (delete the host fast-path):

| Test | Before | Now |
| --- | --- | --- |
| `A_retry_does_not_spend_a_second_assessment` | RED | RED |
| `A_retry_with_the_same_key_and_payload_returns_the_same_queue_id` | **GREEN** | **RED** |

I did it your way — asserting what only the host path can produce (the assessor was invoked once), not the same outcome more loudly. The status assertion I added earlier genuinely would not have worked, and the mutation is what proved it.

**The fake was the real defect, and I fixed that too.** My `RecordingAssessor` never read the spool and never accepted — which is why the suite was green over a broken seam. It now does both, through the container, using the same intake and spool the real pipeline would. Adding that surfaced a second divergence I would otherwise have shipped: my fake invented a fallback idempotency key when the client supplied none, while `MailAssessor` deliberately has none. The fake was **kinder than reality**, so the no-key case looked replay-protected in tests while production would duplicate. Aligned to match exactly.

**Two things I am flagging rather than silently handling:**

1. **A redundant spool copy on every accepted submission.** The host spools so the pipeline can read the bytes; the queue then spools its own copy from those bytes. I am leaving the host's copy in place rather than deleting it — deleting is the one action that could break delivery if my reading of the queue is wrong, and "waste some disk" is strictly better than "maybe lose mail". It roughly halves effective per-tenant spool capacity though, so it is worth a decision: either `QueueSubmission` gains an already-durable reference so the queue can skip re-spooling, or the host deletes after a confirmed accept. Your call; I did not want to guess on the mail-loss side.

2. `ISubmissionIntake.AcceptAsync` is now unused by the host (only the fake and the adapter use it). I left it in place — it is the port to the queue and the fake legitimately needs it — but the host has no production caller, so it could be trimmed if you would rather the port reflect only what the host uses.

Next, per your ordering: **unpause.** I will design it as the audited mirror of pause — same `Administer` privilege, recording who lifted it and why, and I will bring the same "can this test fail?" question to it. Will report when green.
