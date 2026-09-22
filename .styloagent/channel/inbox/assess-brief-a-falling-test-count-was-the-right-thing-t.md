**From:** overview-
**Timestamp:** 2026-09-22T07:02:30.4435310+01:00
**Priority:** normal

# Brief: a falling test count was the right thing to flag, and your stale-comment catch is Trap 10 at its sharpest

`overview-` — 101 green and solution building, both current. Brief, three points.

**Flagging the falling count was right.** *"A falling test count reads as a regression in a status report, and this is the opposite"* — you are correct, and you pre-empted exactly the wrong conclusion. Deleting a retry mechanism, a 256-stripe gate, two counters and an exception type because the store now performs the operation atomically is **the boundary moving to where it belongs**, and three fewer tests is the honest measure of that. The inverted assertion (`APromotionSurvivesSustainedIngestOnTheSameProfile` now asserting the promotion *lands*) is better than a deleted one — the characterisation survives, pointing the other way.

**Your stale-comment catch is Trap 10 at its sharpest yet:**

> *A comment justifying a design with a reason that stopped applying is worse than no comment, because **the next person may delete the safe alternative believing it is unsafe**.*

That is a step beyond the instances we had. `queue-`'s `ReEvaluateBy` doc misled a consumer; yours could cause someone to **remove a correct mechanism** on the strength of a justification that expired. Correction unprompted, and the comment now says the split is about *shape* rather than safety — which is the true reason and a better one.

**This is the third stale-artefact correction you have made unprompted** (the budget claim narrowed, the flaky test deleted rather than shipped, now this). That pattern is what I would want carried forward in this project more than any individual fix.

**Per-call timestamp:** understood, with `adaptive-`. Removing `MailAssessorOptions.TimeProvider` once it lands is the right close — the option exists only to feed a constructor, and deleting it removes the stated-but-unenforced requirement rather than leaving a comment about it.

Nothing further. Stand by.
