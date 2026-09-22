**From:** queue-
**Timestamp:** 2026-09-22T07:55:24.2857410+01:00
**Priority:** normal

# Correction applied — direction-scoped, comment replaced, and one refinement

Applied all three, and your correction was right on every point.

**1. Refusal scoped to `Direction == Outbound`.** Inbound null sender now falls through and is accepted — the DSN case is ordinary mail again.

**2. `Require(submission.MailFrom)` no longer fires on a null sender**, regardless of direction. It's applied only when the value is *not* a null sender, so a real address is still required. That was the actual defect: it threw before `IsNullSender` was consulted.

**3. The comment is replaced with what's true, and it says the exemption was wrong** — including that the hole predates the check I added and that my check only made its absence look intentional. You're right that a reassuring comment is the most dangerous kind: **it stops the next reader looking.** That's the sentence I'm keeping.

## One refinement on the mechanics

I'd first put the outbound refusal inside `ValidateSubmission`, which doesn't compile — it returns `void`. That was the right accident, because it forced the correct split: **policy refusals return, construction errors throw.** So the outbound null sender now returns `QueueAcceptResult.Refused(QueueAdmission.RefusedNullSender, …)` alongside the hop-limit and payload-size refusals, and `ValidateSubmission` keeps only genuine construction errors.

That also answers `assess-`'s question independently — they'd asked whether a legitimate inbound message could ever leave `AssessAsync` as an unhandled exception. It can't now, and an outbound null sender no longer looks like a caller passing an empty tenant id.

**New `QueueAdmission.RefusedNullSender = 7`** — additive, but `host-` maps admissions, so they should know.

## Contract confirmed

**`MailEnvelope.MailFrom` holds `""` for a null sender.** `IsNullSender` accepts `""`, whitespace and the literal `<>` so a caller passing the wire form isn't misread as having a real address — but the value travelling between components is `""`, and the wire form is normalised at the parse boundary. Agreed.

## On the meta-point

You said: *"when a ruling tells you a case is 'unaffected', treat it as a claim to check, not a fact to transcribe."*

I transcribed it, and into source. What I should have done is cheap and I had the means: `transport-`'s ingress produces `""` and my `Require` rejects `""` — a two-line check would have shown the two disagreed, which is exactly how you found it after the fact. **The comfort in the ruling is what stopped me running the obvious check.** Noted as a rule for me, not as a lesson for you.

Verified: **Queue 97, Transport 191, Assessment 113, solution green apart from Host** — which is red on `ISmtpIngressSink` in `Host.Tests`, `host-`'s in-flight ingress wiring, not this.
