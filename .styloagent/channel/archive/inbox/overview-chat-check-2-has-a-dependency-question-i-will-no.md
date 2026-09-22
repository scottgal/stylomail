**From:** chat-
**Timestamp:** 2026-09-22T20:45:08.4830130+01:00
**Priority:** normal

# chat- check 2 has a dependency question I will not decide at the end of a long context

Before writing check 2 I read what it would have to reuse, and there is a question in it that I would rather put to you than answer tired.

## What I found

**`CampaignNearDuplicateDetector.ObserveAndEvaluate` is channel-neutral in its signature.** It takes ids, a timestamp, a `DimensionVector`, a `SecurityBearingFingerprint` and a sender scope. Nothing about it is email.

**And the fingerprint is content-derived rather than semantic**, which is the good news: it digests "the ordered security-bearing components", links and attachments, with the deliberate property that two destinations and one destination twice are different messages.

**The problem is the vector.** The detector compares dimension vectors, and chat has none: all twelve semantic dimensions are recorded `Unavailable` on every chat message by design, because chat is local-only. So a near-duplicate check built on the existing detector would be comparing twelve unavailable dimensions and calling the result similarity.

**The likely answer is that chat's near-duplicate rests on the fingerprint alone**, which its own remarks already guard: `ComponentCount` exists because "a fingerprint over nothing is not agreement", so two link-free messages would not match each other. That needs an overload of `Compute` for `ChatAnalysisInput`, which is a small addition to `assess-`'s file, plus a decision I should not take on my own: **is a link-based fingerprint the right similarity basis for a channel where most messages have no links at all?** For those, check 2 would never fire, which may be exactly right or may mean chat's near-duplicate belongs on text rather than links.

## Why I am asking rather than choosing

This is the check whose inverted intuition you singled out (repetition is how an attack hides), so getting its basis wrong is getting that check wrong in the direction that matters. And I am at the point in my context where I would be deciding it to finish something rather than because I had thought it through, which is the trade you told me not to make.

## What I am doing meanwhile

Nothing on check 2. I will take the parts of the plan that do not depend on this answer, which are check 3 (links and homographs, which reuses the shared analysis already in Core with no new question) and the drain integration with the separated write.

**If you would rather I just pick**: I would build chat's near-duplicate on the content fingerprint with the `ComponentCount` guard, note in the record that link-free messages can never be dismissed as duplicates, and revisit if the dismissal counts show check 2 never firing. That is a defensible default and it is the one I will take if you do not answer before I finish check 3.

Tree is green at 1447 passed, 0 failed, nothing half-written.
