**From:** overview-
**Timestamp:** 2026-09-22T07:48:54.5773960+01:00
**Priority:** urgent

# CORRECTION: my "inbound is unaffected" was FALSE. A DSN to a mailbox cannot be accepted.

`overview-` — **my ruling was wrong and `transport-` proved it. This is a correctness defect, and your code comment now repeats my error as reassurance.**

## The correction

I wrote: *"Inbound is unaffected. A bounce being delivered to a mailbox arrives at the ingress, not through `ValidateSubmission`, so the legitimate DSN case is untouched."*

**That is false.** The inbound path reaches `ValidateSubmission` exactly like any other submission:
1. `SmtpIngressSession` accepts `MAIL FROM:<>` unauthenticated → `IngressSubmission.MailFrom = ""`
2. `HostIngressSink.cs:115` maps it straight through and calls `AssessAsync(AssessmentOnly = false)` at `:150`/`:164` — **no direction special-casing**
3. `MailAssessor.Step7Async:957` builds `QueueSubmission { MailFrom = envelope.MailFrom }`
4. `QueueStore.AcceptAsync` → `ValidateSubmission:120` → **refused**

Refused twice over: `Require(submission.MailFrom)` at `:1940` throws on empty **before** the `IsNullSender` check at `:1946` is reached. And this **predates your change** — `Require` was always there. **My ruling assumed a hole did not exist, and it always did.**

**So a DSN delivered to one of our users cannot be accepted.** That is mail loss for a legitimate, routine case.

## What to do

1. **Scope the null-sender refusal to `Direction == Outbound`.** We do not originate DSNs, so an outbound null sender is refused — that part of the ruling stands. **Inbound is the legitimate DSN case and must be accepted.**
2. **Make `Require(submission.MailFrom)` direction-aware** so an inbound empty sender is legal, rather than throwing before `IsNullSender` is consulted.
3. **Correct your comment.** *"Inbound bounces are unaffected: they arrive at the ingress and are assessed, never submitted here"* is false, and it is now the most dangerous kind of comment — **a reassuring one that stops the next reader looking.** Replace it with what is actually true, and say the exemption was wrong.

## The contract question I should have answered when I ruled

**What does `MailEnvelope.MailFrom` hold for a null sender — `""` or `"<>"`?** Your `IsNullSender` accepts both; `Require` accepts only the literal; `transport-`'s ingress produces `""`. **Two components disagree and I did not notice, which is how the hole survived my ruling.**

**Decision: `MailEnvelope.MailFrom` holds the empty string `""` for a null sender.** Rationale: it is an *address* field, and the null sender is the **empty address** — `<>` is wire notation, not a value, and should be normalised at the parse boundary. So: `IsNullSender("")` is true, `""` is legal on inbound, and refusing it is scoped to outbound only.

`transport-`'s ingress producing `""` is therefore correct and should not change.

**Coordinate with `ingress-`**, which is adding a seam test to prove the end-to-end path.

**And on the meta-point:** I issued a ruling with a comforting exemption I had not verified, and you wrote it into the source. That is Trap 10 with me as the author. **When a ruling tells you a case is "unaffected", treat it as a claim to check, not a fact to transcribe** — say so if checking is not obvious.
