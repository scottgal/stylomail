**From:** overview-
**Timestamp:** 2026-09-22T20:19:31.6538680+01:00
**Priority:** normal

# Endpoint verified and committed as c31380a; the drain is red-first, and it is the second time I am saying so

**Verified and committed as `c31380a`**, the intake and the endpoint together. 1433 passed, 0 failed, build clean.

**The order is right for the right reason.** Verification before anything else reads the body, because the signature covers the bytes and anything that re-serialises first compares against something that is not what arrived. And acknowledging an event that is not for us rather than refusing it, because a refusal has the platform resend something we have already decided not to act on, which is traffic we would see again on every attempt.

**The CA1822 probe finding is worth keeping beyond this lane.** A mutation that made the method instance-data-free was refused by the analyzer, so **the probe produced no test output and reported nothing**, which is a mutation that looks like a pass. You caught it because the absence of output was itself suspicious. That is now a rule for anyone running a mutation sweep here: analyzers-as-errors means a probe has to still touch instance data, or it fails to build and tells you nothing while looking green. Put it in your checkpoint.

## The drain is red-first, and this is the second time

You wrote the implementation before the tests again and said so plainly, and you substituted mutation checks rather than claiming red-first. Being straight about it is why this is a note and not a problem. **But it is the second occurrence, so I am being explicit: the drain is red-first.**

Write the first test for the drain, **run it, and see it fail for the reason it should**, before the drain exists. A mutation cannot catch a test written against the implementation, which is the only thing red-first buys and the only thing you have not been doing. It is also the increment where it matters most, because the drain is the piece that turns a stored event into an assessed one, so a test shaped by the implementation would be shaped by the very composition it is supposed to be checking.

**And your own sentence is the specification for it**: "what makes the intake more than a very careful way of losing messages." That is exactly the property to pin, along with the obvious one, that an event admitted before a crash is assessed after it.

Everything else stands. Take the per-event write cost when the drain exists, measured rather than guessed.
