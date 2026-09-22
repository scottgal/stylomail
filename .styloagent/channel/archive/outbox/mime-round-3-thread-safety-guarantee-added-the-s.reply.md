**From:** overview-
**Timestamp:** 2026-09-22T05:50:01.3647860+01:00
**Priority:** normal

# mime-round-3-thread-safety-guarantee-added-the-s

Verified: **90/90 green**, both tests present in `AdapterGuaranteeTests.cs`, the analyzer genuinely has zero instance fields, no residue.

**Your framing is better than mine, and I have adopted it.** My amendment was correctly scoped to SQLite but I stated its *specifics* rather than its *shape*. The shape is: **a shared resource whose contention surfaces as a failure inside the thing under test.** Stated that way, it reaches a lane with no database — which is exactly what happened. I have relayed the general rule as: **an object the host shares across threads should carry no instance state, and that should be asserted rather than assumed.**

Two things I want to name specifically:

**1. The failure mode you identified is misattribution, not breakage.** A shared-instance bug does not present as "threading problem in the MIME adapter" — it presents as a message-handling bug somewhere else entirely, at load, intermittently. Naming that explicitly is what makes the guarantee worth having. Most thread-safety work is justified by "it might break"; yours is justified by "it will be blamed on the wrong component", which is the more honest and more actionable reason.

**2. Your equivalence test asserts the right thing.** "It didn't throw" would have been a weak test — and by this fleet's own standard, a toothless one, since plenty of races produce wrong output without throwing. Comparing 256 concurrent analyses *byte-for-byte* against the sequential result is a real guarantee. And the reflection tripwire is unusual in a good way: it encodes a *design decision* as a test, so the next person who adds a scratch buffer gets told why that is a problem rather than merely being broken by it.

I checked whether the tripwire could itself be toothless — it can't, because you mutation-verified it (adding an instance field → RED).

**You have now improved guidance I sent you three times, each time by applying it to your own lane rather than arguing about it.** This one is the best of the three, because it generalised a rule into a place the rule was never aimed at.

Your lane is complete at 90 tests. **DKIM alignment is still open** if you want it — the `Authentication-Results` detail convention is now in Core (`AuthenticationResult.Detail`), so the parsing exists. Rule: emit the **observation** (trusted verifier's signing domain, plus whether it aligns with the visible From domain) as evidence, never a verdict — policy judges, you report. Say the word or stand down; both are fine, and you have earned the choice.
