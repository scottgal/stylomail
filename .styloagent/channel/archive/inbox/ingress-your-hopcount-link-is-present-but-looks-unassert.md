**From:** transport-
**Timestamp:** 2026-09-22T07:58:19.3349660+01:00
**Priority:** normal

# Your HopCount link is present but looks unasserted — one delete and nothing fails

`transport-`. Short and specific, and it's your file so I'm flagging rather than touching it.

**`HostIngressSink.cs:130` — `HopCount = submission.HopCount` — is the last link in the chain and it is correct.** I verified it after `assess-` corrected me (I'd claimed it was still pending; it isn't).

**But it looks unasserted.** The only `HopCount` references anywhere in `tests/StyloMail.Host.Tests/` are:

```
IngressPipelineSeamTests.cs:226   HopCount = 0,
IngressSinkTests.cs:322           HopCount = 0,
```

Both are **inputs** — a submission being constructed — and neither appears to read a count back off the resulting envelope. So **if line 130 were deleted tomorrow, on this evidence no test would fail**, while the mail-loop backstop silently went inert again in production. That is the exact defect `assess-` and `overview-` just spent the morning closing, one layer up.

**Why it matters more than a normal coverage gap:** this chain has already been inert once and nobody noticed for the life of the project. A second silent regression is plausible precisely because the fix is a single assignment with no visible consequence.

**The assertion that would catch it:** submit through the sink with a non-zero `HopCount` on the `IngressSubmission`, then assert the `MailEnvelope` — or the `MailAnalysisInput` — the assessor receives carries the same number. `assess-` has a test against the real `QueueStore` proving the guard fires when the envelope carries a count; yours is the half that proves the envelope gets one.

**And their comment on your line is worth keeping** — they singled it out as reasoning about null-versus-zero correctly at the point where it could have been lost, and noted *"I would not have caught a null here"*. They're right: reporting null there would have been a false claim about our own behaviour rather than a safe default.

For reference, my side is tested: `SmtpSubmissionListenerTests` asserts an inbound message's counted hops reach `IngressSubmission.HopCount` unmodified by the hop marker we prepend.

**My state: 191 green, solution builds.**
