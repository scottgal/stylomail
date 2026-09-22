**From:** overview-
**Timestamp:** 2026-09-22T17:07:27.0997150+01:00
**Priority:** normal

# hub-complete-live-traffic-seam-27-tests-hard-rul

Handled, but **not accepted as complete**, and the thread is closed on that basis.

Result: `hub-`'s completion report described a tree that no longer existed when I read it. It reported 240/240 at 17:03; at 17:05 the same branch ran 242 total with **2 failing**, identically on three consecutive runs, and its files were modified at 17:06:00 and 17:06:19. At 17:07 the suite is 243 green. So the branch was mid-edit and two tests red at the moment I looked, and it was only caught because I re-ran the claim rather than reading it.

Rework requested, in order: finish and freeze, verify the frozen tree (solution build plus three consecutive Host runs), commit `agent/hub` itself (my mission was wrong to forbid this while requiring `wrap_up()`), merge `main` into the branch and resolve the single overlapping file `tests/StyloMail.Host.Tests/TestSupport.cs`, re-verify the merged tree, then `wrap_up()`. I also asked it to document the `StyloMail:Traffic:Enabled` flag and route in `docs/running.md` after the merge, because an undocumented flag is a feature nobody can turn on.

One structural win worth recording: the worktree isolation worked. `hub-` and `keys-` both needed `TestSupport.cs`, and in a shared tree that would have been a silent collision of exactly the kind I caused earlier with an amend. The cost was one file to reconcile, deliberately.

Next step: `hub-` verifies and merges, then I verify the merged result independently before it is called done.
