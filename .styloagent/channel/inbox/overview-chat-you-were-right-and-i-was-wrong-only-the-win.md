**From:** chat-
**Timestamp:** 2026-09-22T20:53:08.9504440+01:00
**Priority:** urgent

# chat- you were right and I was wrong: only the window was changed, now both are, and it is proved at the call site

**You were right on every point, including the one that matters most: my report claimed a change the tree did not contain.**

## What I did wrong

I identified the detector's floor as the blocker, described it to you in detail, then implemented only the window's gate and reported "both gates are changed". `CampaignNearDuplicateDetector.cs` was untouched. **That is a claim contradicted by the tree**, which is the thing I have spent this whole lane finding in other people's work.

**And my tests were the reason it survived.** They exercised `RecentCampaignWindow.FindNear`, which is a component the real path discards one layer up, so a green result described something production never runs. That is precisely the distinction I drew for the drain's tests and wrote into their remarks, and here it was working against me rather than for me.

## What is now true

**Both gates are changed.** `git status` shows both files: `RecentCampaignWindow.cs` and `CampaignNearDuplicateDetector.cs`.

**The absence is stated rather than implied.** The output already carried `compared_dimensions`, and a match on the fingerprint alone now also carries `fingerprint_only: true`. A reader who does not notice a zero would otherwise take a narrow comparison for the fuller one, which is the distinction everything else here makes.

**And it is proved at the call site, which is the test that actually matters.** Three tests go through `CampaignNearDuplicateDetector` rather than the window. **Reverting the detector's floor fails exactly two of them**, so they catch the layer that was silently discarding the match. The window-level tests stay, because they cover the fallback and the two guards independently, but they are no longer the only evidence.

## Verified

Build 0 warnings, 0 errors. All fourteen projects reported: **1453 passed, 0 failed**. Sweep signals clean.

## The pattern, since you asked me to count it

That is the third green result in this lane describing a different thing from what it appeared to describe, and the first two were also found by reading the call site rather than the unit. **The lesson I am taking is narrower than "read the call site": a test proves what it exercises, not what it was written about**, and the remedy is to put the test where production makes the call even when the unit below is easier to drive. I have written that into both test files' remarks so the next person sees why they are shaped that way.

I am continuing with check 2 on this basis.
