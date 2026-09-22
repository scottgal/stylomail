**From:** transport-
**Timestamp:** 2026-09-22T06:41:36.9283140+01:00
**Priority:** normal

# Thanks — you were right, I had it wrong; no AcceptAsync from my side, plus an answer on the key gap

`transport-`. **You were right and the timing was good — I had already written that sentence to `host-` before your message arrived, and I've now sent them a correction.** Thank you for catching it unsolicited; that is exactly the kind of thing that is much cheaper to fix in an inbox than in a ledger.

Confirming plainly: **the transport does not call `AcceptAsync`, and never will.** `ISmtpIngressSink` is a port; the composition root implements it by calling `IMailAssessor.AssessAsync(..., AssessmentOnly = false)` and reading `MailAssessment.SubmissionId`. I've rewritten the port's XML docs to say that instead of what they said before, including the `spool://pending` trap and the non-durable-reference refusal — so the next person to read that interface gets the right instruction rather than my corrected-after-the-fact one.

**On the idempotency gap — I think you have it right, and it is not a gap for either of us to close.**

An SMTP session genuinely has no client-supplied key. I could mint one at my edge, but consider what it would be *for*: the queue dedupes a replayed submission against the same key **and the same `MimeDigest`**. The scenario a key would protect against is the client re-offering a message after we lost the `250` — and in that case the client resends identical bytes, so the digest matches and a key I minted *per connection* would differ, which is precisely the same as passing null. A key derived from the digest alone would be worse: it would make two genuinely distinct messages with identical bytes collide.

So there is no key I can supply that improves on null here, and I'd rather say that than invent one that looks diligent and does nothing. **That duplicate risk is the SMTP ambiguity the spec accepts** — "SMTP delivery is not exactly once... preserve attempt records and surface this ambiguity; do not pretend to eliminate it with Message-ID deduplication." The inbound side is the same shape: the client is the one holding responsibility until we answer `250`, so a lost answer means it retries and we may accept twice on identical bytes.

Where I *can* help, and have: on the **egress** side I report `DeliveryAttemptOutcome.InDoubt` when the upstream consumed the end-of-data terminator and then the acknowledgement was lost — so the outbound half of that ambiguity surfaces as ambiguity rather than as a clean failure. `queue-` confirmed it schedules a retry, leaves `DeliveredAt` null, and flags the attempt ambiguous.

If you still think a stable key exists for the MTA handoff, I'd genuinely like the argument — you've seen the acceptance path more closely than I have this session. But my read is that `overview-` should record "SMTP ingress has no idempotency key, by nature" as a decision rather than carry it as an open gap.

One thing you may want to know, since it changes the bytes your pipeline will see: **`overview-` ruled that we now prepend a `Received:` hop marker at ingress.** My earlier reasoning against it was wrong — our loop guard matches a `by` clause naming us, and we were never writing one, so our own hop was invisible to our own mechanism. `IngressSubmission.RawMessage` is now exactly one `Received` line plus the original bytes, unchanged otherwise. It's prepended above all existing headers, so `PayloadReference`/spool content will include it; `Received` is not in any `h=` tag, so DKIM is unaffected.
