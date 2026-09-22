**From:** chat-
**Timestamp:** 2026-09-22T20:50:15.5377510+01:00
**Priority:** normal

# chat- Option A implemented and measured: the email behaviour DID change, and there was a second gate you did not know about

Option A is implemented and the measurement you demanded is taken. Two things you should know, one of which is that the change was bigger than the floor.

## There was a second gate

**`RecentCampaignWindow.FindNear` skips every comparison with no dimensions to compare:**

```
if (compared == 0 || similarity < minimumSimilarity) { continue; }
```

So changing only the detector's floor would have changed nothing: a chat message, and an email message during an outage, never produce a candidate match to filter in the first place. **Both gates are changed**, and the fallback is guarded on both sides having components, because a fingerprint over nothing is not agreement and two unrelated quiet messages would otherwise match each other.

## The measurement, and it is the case that changed

**The email suite is unchanged**, and the suite passing is *not* evidence of inertness. It means nothing covered the case, which is exactly what you said to check rather than assume.

**So I established it directly: reverting the window change makes the new test fail.** The behaviour did move.

**An email message assessed during a semantic outage now matches on its security-bearing fingerprint where it previously could not match at all.** Today those messages fail on zero comparable dimensions and the campaign window is effectively off while the classifier is down. That is arguably an improvement, and it is a change to shipped email behaviour in a degraded state, so per your ruling it needs **its own justification in the commit message** rather than arriving as a side effect of a chat task. The justification I would write: the campaign window's floor exists so a match is not declared on too little evidence, and for a message with no dimensions the fingerprint is the evidence, so the floor's own stated ground is satisfied by a different signal rather than waived.

Three tests pin it, including the two guards: an empty fingerprint still agrees with nothing, and different fingerprints never match on the fallback. The last of those is the fifty-first message, which now escalates **by construction rather than by a threshold**, which is what your ruling asked for.

## What is not done yet, and is next

**The output does not yet state the absence.** You required that a comparison matching on the fingerprint alone says no dimensions were compared, and I have not written it. It is the next thing I do, before check 2 uses any of this.

## Verified

Build 0 warnings, 0 errors. All fourteen projects reported: **1450 passed, 0 failed** (three new). Sweep signals clean, no em-dashes in anything added.

I am reporting this before finishing the output statement because the measurement was the gating item and you asked for it explicitly, not because I am stopping.
