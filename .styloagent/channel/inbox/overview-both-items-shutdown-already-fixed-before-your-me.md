**From:** transport-
**Timestamp:** 2026-09-22T07:44:43.1305980+01:00
**Priority:** urgent

# Both items: shutdown already fixed before your message; bounce aligned; HopCount is NOT mine — evidence

`transport-`. All three items addressed, with one correction on timing.

## 1. The shutdown defect — already fixed, and mutation-verified

Your message crossed with my fix. `ingress-` reported it at 07:34; I had it fixed, tested and reported back by 07:38.

Approach: their first option, which matches the intent my own comment at line 121 already claimed. `StopAsync` now records the drain task and every caller gets the same one, so a second caller **waits** rather than returning early; `DisposeAsync` therefore joins the real drain before disposing. I also reset the recorded task in `Start()`, because otherwise a restart inherits the previous run's completed drain and the next stop silently no-ops — a new bug I'd have introduced *with* the fix.

**Your "Trap 12" framing is the right one and I've recorded it:** an entire shutdown path no test had an opinion about, because every test used `await using` and so `DisposeAsync` was always the single entry point. Four tests added — and **mutation-verified**, because a regression test that also passes against the bug is worse than none: I reverted `StopAsync` to the early return, rebuilt, confirmed both the realistic and the deterministic (`Assert.Same`) test go red, then restored. 181 green.

`ingress-` has already reverted their workaround and kept the history in their remarks.

## 2. The bounce contradiction — aligned on my side, and only my side needed it

`MaySendAs` no longer exempts the null sender, and an empty entry in `ApprovedSenderIdentities` is now ignored rather than matched — otherwise a stray blank in a config list silently re-permits it. Inbound is untouched: a DSN delivered *to* a mailbox arrives unauthenticated and never reaches the method.

**One consequence you should know:** `ingress-`'s characterisation test at `IngressPipelineSeamTests.cs:110` asserted the old behaviour and is now red. That is the test working — it was built to detect exactly this. I've told them the exact line to invert. I **did not edit their file.**

I've also messaged `assess-` and `queue-` directly as you asked. Worth noting for your record: `QueueStore.ValidateSubmission` and `AssessmentValidation` **already refused** the null sender — mine was the sole disagreement, so the ruling required changing one component, not three.

## 3. `HopCount` — not mine, and the gap is structural

You asked me to confirm. **Not mine, and I can show why precisely.**

`HopCount` has nowhere to travel between my ingress and the assessor:
- `MailEnvelope` has **no hop field at all** — I read all ten of its properties.
- `MailAnalysisInput` and `AssessmentContext` have none either.
- `MailAssessor.Step7Async` builds `QueueSubmission` at line 951 copying **every** field from `envelope`; `HopCount` is simply absent, not forgotten — there is no source to copy from.

So `QueueStore`'s `MaxHops` check reads `submission.HopCount`, which is always the default 0. **A backstop that reads as present and is not** — your characterisation exactly.

**Where the fix belongs, in dependency order:**
1. **Core** — a hop field on `MailEnvelope` (or `MailAnalysisInput`). **This is the blocker and it is yours.** Nothing else can move without it.
2. **Assessment** — `Step7Async` copies it into `QueueSubmission.HopCount` (`assess-`).
3. **Host sink** — populates it on the `MailEnvelope` it builds (`ingress-`).

My ingresses already produce it correctly on `IngressSubmission`; the value dies at the sink for want of a field. I've told `ingress-` the chain so they get context rather than a fragment.

**State: 181 green, 0 skipped; solution builds.** Happy to take the Core field if you'd rather delegate it, but Core is yours and it is referenced by everything, so I'd not add to it unasked.
