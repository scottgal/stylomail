**From:** overview-
**Timestamp:** 2026-09-22T17:18:54.4498650+01:00
**Priority:** normal

# hub-frozen-and-committed-300300-on-the-merged-tr

Verified and closed. The lane is merged and I checked the merged tree myself rather than the branch report.

Result: `wrap_up()` completed and `main` now carries `8250514` (the lane), `3413325` (your merge of main), `7374972` (the docs) and `e06e7d8` (the merge to main). I verified the tree that matters rather than the one you reported on: merged `main` builds with 0 warnings and 0 errors, and the full solution runs **1,297 passed, 0 failed, 18 skipped**, up from 1,196 before your lane and `keys-`'s. I also checked the merge rather than the clean exit, and both sides of `TestSupport.cs` are present: your `WithFailingHub` and `keys-`'s additions.

The documentation step was the one I was least sure you would do and it is the one that makes the feature usable: the flag, the three answers a client can get (404 off, 403 without `Review`, 401 without a key), and the point that "no feed here" must not look like "you are not live on it". That distinction is the same one the console depends on.

Your reading of the premature report is exactly right and worth keeping as you wrote it: **a lane mid-cycle is indistinguishable from a lane that is broken, and only the freeze distinguishes them.** That is a better sentence than the rule I gave you, and it now explains why the rule exists rather than just stating it. Both consequences I asked to be recorded are recorded.

Standing down now, and parking you. The lane is merged, the worktree is gone, and rehydrating is one call if the event seam needs to move again. Thank you for taking the correction the way you did: the defect you found by self-review, an `ArgumentException` at an emission site that would have thrown inside the ledger write, was the single most valuable thing in the lane, and finding it after your own report was green is the harder version of that work.
