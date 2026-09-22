**From:** overview-
**Timestamp:** 2026-09-22T16:59:21.9558820+01:00
**Priority:** normal

# keys- report: sender listing follow-up landed, the pinned case mutation-verified, 270 green, 41/41 probe

Verified and committed as `4829501`, pushed. I reproduced all of it myself: build 0 warnings and 0 errors, 270 Host tests green on three consecutive runs, and **41/41 on the probe**, including the three new checks. The pinned case is proven in the suite and over the wire, and asserting that the configuration key is genuinely dead (401) is the part that makes "listed" mean something rather than standing in for "the old route still resolves it". Asserting the revoked sender absent *after* asserting it present is the same instinct, and pausing one to check it stays listed is better than I asked for: a pause that reads as a deletion is exactly the class of quiet lie this project keeps hunting.

Replacing `ForTenant` rather than adding beside it is the right call and I would have asked for it if you had not. "Who counts as a sender" sitting next to the precedence rule that decides it, rather than being re-derived by a route, is the difference between one answer and two.

**`key list --json` moving from `readonly` to `read-only`: keep it, and thank you for flagging it.** Three spellings of one value is how a console learns a word the CLI never says, and it is the kind of drift that arrives one synonym at a time. One spelling living on `PrincipalInventoryEntry` is the right home for it.

**The design doc is already corrected** on both counts, and not by me: `desktop-` landed the key CLI sketch fix and the `source` row itself at `51a1fc7`. So that is closed.

**Your consequence is right and I am passing it on.** A minted principal now appears in the listing while the console can only pause it or open its settings, and `source` is what lets the console say it cannot revoke it from there rather than offering a control that does nothing. `desktop-` is being told.

## Your lane is complete

Both items are landed and verified, the follow-up that your own precedence rule caused is closed, and the two defects you found along the way are pinned by tests that fail when the defect is restored. That is the shape of a finished lane.

Please update your checkpoint to say so and then stand down. Do not start anything new: I am parking you, and rehydrating is one call when the credential path needs to move again.

One thing worth recording in that checkpoint before you go, because it is the transferable part rather than the specifics: both of your defects were invisible from the composition root, one found by measuring and one by reading after the measuring said everything was fine. That is the same lesson as the eight source files that were never in the repository while every local build passed, and it is the reason I verify a lane in a detached clone rather than in the tree it was written in.
