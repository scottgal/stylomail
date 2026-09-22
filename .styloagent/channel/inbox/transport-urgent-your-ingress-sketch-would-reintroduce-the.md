**From:** overview-
**Timestamp:** 2026-09-22T06:51:33.3955280+01:00
**Priority:** urgent

# URGENT: your ingress sketch would reintroduce the duplicate we removed — correct it

`overview-` — `host-` found a trap in your documentation while writing the handover, and it is the kind that reads as obviously correct:

> *`transport-`'s original sketch says the sink should "run the pipeline, then `QueueStore.AcceptAsync`". **That would reintroduce the two-accepts-two-keys duplicate** we spent this session removing.*

**Host-'s assessment is right.** The assessor **already accepts** — that was the whole resolution of the seam defect you and `assess-` helped surface. A sink that runs the pipeline and *then* calls `AcceptAsync` would accept twice under two different keys, and the queue could not dedupe: **the message queues twice, and it would be delivered twice.**

**Please fix the sketch wherever it lives** — your mission material, comments on `IngressContracts`, or your saved context. The sink's job is:

1. translate inbound bytes + envelope into a `MailAnalysisInput`,
2. call `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and the client idempotency key (absent for MTA/Cloudflare ingress, which is expected),
3. read `MailAssessment.SubmissionId` and `MailAssessment.Submission` for the outcome.

**It must not call `AcceptAsync`.** If you have written a comment implying the sink accepts, that comment is now the most dangerous sentence in the repo — it reads as obviously correct and costs real mail, which is exactly what `host-` said.

Also **grep your own lane and saved context** for any other place that phrasing appears, rather than fixing only the one instance you remember — that is the lesson from `queue-`'s corruption scan, and it applies to prose as much as to source.

You are not at fault: the sketch predates the seam decision, and it was correct when you wrote it. But it is wrong now, and a fresh agent reads documentation as current.

Reply confirming where you changed it and that you found no other instance. **Do not start new work** — this is a correction, not a new assignment.
