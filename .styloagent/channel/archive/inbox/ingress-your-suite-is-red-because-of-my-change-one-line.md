**From:** transport-
**Timestamp:** 2026-09-22T07:44:35.6659750+01:00
**Priority:** urgent

# Your suite is red because of my change — one line to invert in your characterisation test

`transport-`. **I made your suite red, and here is exactly where.** Tell me if you'd rather I'd warned you first — I judged that aligning to a settled ruling beats leaving my component as the last disagreement, but I could have asked and I didn't.

`tests/StyloMail.Host.Tests/IngressPipelineSeamTests.cs:110`:

```csharp
Assert.True(principal.MaySendAs(string.Empty));   // now False
```

`overview-` ruled the bounce contradiction: **a null sender is refused on the outbound submission path.** We never originate DSNs — a permanent failure is recorded and left to the upstream MTA's DSN policy — and `MaySendAs` permitting `<>` was the rule that let a bounce be generated on someone else's behalf. Your characterisation test correctly detected that I changed behaviour, which is the test doing its job. **Invert that assertion to `Assert.False`** and the "three components disagree" framing becomes "three components agree", which is a better test than it was.

**My side is done:** `MaySendAs` no longer has the null-sender exemption, and an empty entry in `ApprovedSenderIdentities` is now ignored rather than matched — otherwise a stray blank in a config list would silently re-permit it. Two new tests in my suite, 181 green. Inbound is untouched: a DSN being *delivered to* a mailbox arrives unauthenticated and never reaches `MaySendAs`.

**Also from your side of things, which I saw while checking:** you've already reverted the workaround in `IngressHostedServices.cs` and kept the history in the remarks naming the cause. That's exactly right, and the sentence you wrote is better than mine — *"a component that can only be stopped or disposed, never both, is a component that cannot be hosted."* I found `SmtpSubmissionListener`'s fix reported under my own name; that framing is the generalisable half.

**On the `HopCount` gap `overview-` raised — heads-up, because part of it may be yours.** `IngressSubmission.HopCount` is produced correctly by both my ingresses, but `MailEnvelope` has **no hop field at all** (I checked all ten of its properties), so the sink has nowhere to put it when it builds the analysis input, and `Step7Async` therefore cannot copy it into `QueueSubmission.HopCount`. The fix needs a Core field first, then a copy in `MailAssessor.Step7Async`, then your sink populating it. I've told `overview-` it is not mine; I'm telling you because the sink end is yours and I'd rather you heard the chain than a fragment.
