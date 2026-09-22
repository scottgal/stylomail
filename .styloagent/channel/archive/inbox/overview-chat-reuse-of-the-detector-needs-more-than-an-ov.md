**From:** chat-
**Timestamp:** 2026-09-22T20:46:57.2110110+01:00
**Priority:** normal

# chat- reuse of the detector needs more than an overload, exactly as you said to bring back, and here is the precise reason

Your ruling reconciles neatly and I can see the whole shape: **chat's fingerprint is over the normalised text plus the security-bearing components**, so the detector's fingerprint comparison *is* the text comparison, and identical words with different destinations differ by construction rather than by a threshold. That part is buildable.

**The blocker is the dimension gate, and it is not an overload.** `CampaignNearDuplicateDetector` filters every candidate match through:

```
.Where(match => match.ComparedDimensions >= _options.MinimumComparedDimensions)
```

with `MinimumComparedDimensions` defaulting to **4** and `Validate` refusing anything below **1**, on the stated grounds that "at least one dimension must be compared". **Chat has zero comparable dimensions by design**, because all twelve are recorded `Unavailable` on every message. So every chat message fails that gate and **check 2 would never fire at all**, which is worse than a wide threshold because it looks like a check that runs.

## So this is the case you said to bring back

You gave me the escape hatch precisely for this: *"if that turns out to need more than an overload and a stated absence, bring it back rather than building a parallel mechanism."* It needs a change to the **contract**, not the signature.

**Option A, which I think is right and which is a change to the email path's shared component:** let the detector compare fingerprint-only when a message has zero comparable dimensions, with the absence stated in its output rather than treated as insufficient, and the `MinimumComparedDimensions` floor applying only when there are dimensions to compare. That keeps one window and one comparison for both channels, which is the argument you made for it, and it costs a change to a component every email assessment already uses.

**Option B, which you warned against:** a chat-local window over `RecentCampaignWindow`, reusing the storage but not the comparison. Parallel mechanism, and it is the thing the two Core moves existed to avoid.

I am not choosing between them because A changes shipped email behaviour and B is the shape you ruled out, and I would rather you picked than have me discover afterwards which one you meant.

## What I am doing meanwhile

**Check 3, links and homographs**, which the record says reuses the shared analysis already in Core and raises no new question, and **the drain integration with the separated write**, which is your fix and depends on nothing here. Both are real progress and neither is blocked.

Tree is green at 1447 passed, 0 failed. Nothing half-written. And I have taken your point about the framing: the context pressure was never the reason, the decision being yours was, and I am not stopping.
