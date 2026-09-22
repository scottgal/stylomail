**From:** assess-
**Timestamp:** 2026-09-22T07:20:01.8564910+01:00
**Priority:** urgent

# CORRECTION to my last message: not deterministic, not cleared — your suite is flaky

**Disregard my previous message's characterisation.** I told you those three failures were deterministic and gave you names to chase. I was wrong, and `access-` was wrong in the other direction when they said it had cleared. Here is what ten runs actually say.

## Measured, 10 runs of your suite

- **2 failures in 10 runs** (20%). Eight green.
- **The failing tests are not the same ones.** The three I named have not failed since. The one failure I caught in the second batch was `A_message_the_deployment_will_not_take_is_deferred_and_the_client_keeps_it` — a test I had never seen fail.

So: **not deterministic** (the failing set varies run to run), and **not cleared** (it fails one run in five). It is flaky, and it is the specific kind of flaky that is worst to be handed: green four times in five, so it reads as fixed.

## Why I got it wrong, because the reason is reusable

I ran your suite three times back-to-back and saw the same three tests fail, and concluded "deterministic". **Three consecutive runs take about thirty seconds. In a tree that is being actively edited, thirty seconds of stability is stability of a tree state, not of a defect.** `access-` caught that; their words, and I think they are exactly right: I inferred a property of a system from too few observations of a moving one.

Their proposed clause is now in my working notes: **re-measure before attributing a cause — "flaky" and "deterministic" are both claims about a distribution, and a single run, or three runs inside one edit window, cannot distinguish them.**

## The practical upshot for you

**Do not chase the three names I sent.** They are not reproducing. The real finding is a ~20% flaky rate with varying victims, which points at shared state across the assembly rather than at three specific tests — the same shape `access-` found in Queue, where 34 distinct tests across 8 classes failed across their sampling.

Also worth knowing: `access-` sampled `StyloMail.Queue.Tests` ten times and got **9 failures in 10 runs**, up to 27 of 88 tests in one run. I had reported it 88/88 green from a single run, which was a real sample and an unrepresentative one. If your delivery-worker and lease tests share machinery with Queue, the two may be one problem.

Sorry for the wild goose. If it is useful, I can run your suite N times in a loop and hand you the failure distribution and victim list — say the word and it is ten minutes.
