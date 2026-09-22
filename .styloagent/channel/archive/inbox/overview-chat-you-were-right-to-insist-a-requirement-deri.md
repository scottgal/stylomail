**From:** chat-
**Timestamp:** 2026-09-22T20:25:34.9459600+01:00
**Priority:** normal

# chat- you were right to insist: a requirement-derived test found a real defect in the drain, a retry with no backoff

You were right to insist, and it paid immediately. The drain had a defect that neither my mutation checks nor my own reading had caught, and a test written from the requirement rather than the code found it in one run.

## First, honestly: I could not do what you asked literally

The drain already existed, so I could not write its first test before it and watch it fail. You ruled on the identical situation with Task 2: *"You cannot un-see the implementation, so a red run now would be theatre."* So I did not stage one and claim red-first.

## What I did instead, which is the closest available thing and it worked

I wrote two tests **from your requirement text rather than from the code**: one for the property you named, that an event admitted before a crash is assessed after it, and one for the claim in the drain's own remarks, that *"the next pass picks it up again, so a transient fault clears itself"*. I ran them before assuming they would pass.

**The crash test passed. The transient-fault test went red for exactly the right reason**, and it was not the test that was wrong:

```
Assert.Single() Failure: The collection was empty
```

The event was not still waiting after the failing pass, because **the drain retried it in a tight loop.** The loop re-takes the same batch whenever there is work, so a failing event is offered again immediately, forever. A persistently failing event, which is what a storage outage or a rejected credential looks like, would spin as fast as the CPU allows, hammering the assessor and the database for the whole duration of the fault.

**That is precisely the class you said red-first catches and a mutation cannot**: the test was derived from what the drain *claims*, and the claim and the code disagreed. My mutation checks all passed because they only asked whether the tests noticed changes I chose to make.

## The fix

**The pass now paces itself when it makes no progress.** `AssessAsync` returns whether the event was dealt with, the loop counts progress, and a pass that completed nothing waits the same idle interval an empty intake uses. Both cases are the same thing: there is nothing useful to do yet, so waiting is right for both, and the retry of a poisoned event is now paced rather than a spin.

## Verified

Build 0 warnings, 0 errors. Whole solution **1441 passed, 0 failed, 23 skipped**: four new drain tests, one of which was red before the fix. Sweep signals clean.

## And the standard for what comes next

I am carrying this forward rather than noting it: **for Task 5's completeness pass and the per-event write measurement, the tests and the measurement come from the requirement text and run before I look at whether they pass.** The lesson is not that I should have written the drain's tests first, it is that a test derived from the requirement disagrees with the code often enough to be worth the run every time, and this one disagreed on the first attempt.
