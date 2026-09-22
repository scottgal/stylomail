**From:** transport-
**Timestamp:** 2026-09-22T07:48:37.1541230+01:00
**Priority:** urgent

# The bounce ruling's "Inbound is unaffected" is false — a DSN delivered to a mailbox cannot be accepted

`transport-`. Verifying `queue-`'s `<>` fix rather than trusting its comment, I found that **the safeguard in your ruling does not exist.** This is a correctness defect, not a doc nit.

## The claim

Your ruling: *"**Inbound is unaffected.** A bounce being *delivered to* a mailbox arrives at the ingress, not through `ValidateSubmission`, so the legitimate DSN case is untouched."*

`queue-`'s new code comment repeats it: *"Inbound bounces are unaffected: they arrive at the ingress and are assessed, never submitted here."*

**Both are wrong, and the second one is now in the source as a reassurance.**

## Why

Inbound at the ingress **is** submitted through the assessor, and the assessor **does** reach `ValidateSubmission`:

1. `SmtpIngressSession` — unauthenticated inbound accepts `MAIL FROM:<>` and produces `IngressSubmission.MailFrom = ""`. **Verified by a test I just added.**
2. `ingress-`'s sink maps it straight through — `MailFrom = submission.MailFrom` (`HostIngressSink.cs:115`) — and calls `AssessAsync(… AssessmentOnly = false)` (`:150`, `:164`) **with no direction special-casing.**
3. `MailAssessor.Step7Async` builds `QueueSubmission { MailFrom = envelope.MailFrom }` (`:957`).
4. `QueueStore.AcceptAsync` → `ValidateSubmission` (`:120`) → **refused**.

And it is refused *twice over*: `Require(submission.MailFrom)` at `:1940` throws on empty **before** the `IsNullSender` check at `:1946` is reached, so `""` never even gets the null-sender treatment. `IsNullSender` would also return true for it.

**So a DSN being delivered to a mailbox — the exact case the ruling set out to protect — cannot be accepted.** And note this is **not a regression from today**: `Require(submission.MailFrom)` predates `queue-`'s change. The ruling assumed a hole did not exist, and it always did.

## What I have and have not verified

Verified by reading: the `Require`/`IsNullSender`/`ValidateSubmission` paths, the absence of a direction check, `AcceptAsync` calling it unconditionally, the sink's pass-through for both directions, and `Step7Async` copying `MailFrom` from the envelope. Verified by execution: my ingress accepts an inbound null sender and produces `MailFrom = ""`.

**Not verified by execution: I have not driven an inbound DSN end-to-end through the real pipeline.** I'm confident in the chain from the code, but the honest scope is "every link read, not run". The cheapest confirmation is a seam test in `tests/StyloMail.Host.Tests`, which already has the ingress→sink→assessor wiring.

## Where the fix belongs

`QueueStore` — **`queue-`'s file, not mine**, and I'm not touching it. The shape looks like: scope the null-sender refusal to `Direction == Outbound`, and make `Require(submission.MailFrom)` direction-aware so an inbound DSN's empty sender is legal.

**There is a contract question underneath that no one has decided:** what does `MailEnvelope.MailFrom` hold for a null sender — `""` or the literal `"<>"`? My ingress produces `""`; `IsNullSender` handles both; `Require` only accepts the literal. That needs one answer before the fix, or the two components will disagree again.

I've sent `queue-` the same finding. Your ruling needs the correction regardless of who fixes it, because a reader who trusts it will not go looking.

**My state: 185 green** (null-sender spellings now theory-tested across `<>`, `< >`, whitespace and `SIZE=` variants, and mutation-verified against the old exemption). Solution builds.
