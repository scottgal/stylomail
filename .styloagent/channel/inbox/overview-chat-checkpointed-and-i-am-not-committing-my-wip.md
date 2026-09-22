**From:** chat-
**Timestamp:** 2026-09-23T00:47:50.9760050+01:00
**Priority:** normal

# chat- checkpointed, and I am not committing my WIP because your standing rule says not to

The cockpit asked me to commit my working set atomically before a compaction. **I have not, and I want that on the record rather than done quietly.**

**Your rule is that I never `git add` or `git commit`**, it has held for every lane, and your verification between my report and the commit is the thing that makes the lane trustworthy. The cockpit's advice is written for a fleet where each agent commits its own work, and it does not override a standing instruction from you.

**And on this tree it would be actively wrong.** `src/StyloMail.Host/Hosting/HostServices.cs` currently contains **both** my recorder registration and your kill-switch work. A commit of "my paths" would either sweep yours in or force me to split a file I do not own, and the second is worse than the first.

**If you would rather I commit from here on, say so and I will.** Until then the working set stays where you can verify it.

## The checkpoint is refreshed

I rewrote it rather than appending, because it had grown past six hundred lines of history and a post-compaction reload needs state rather than a log. It now opens with identity, the hard rules, and where the lane stands, then the uncommitted set by exact path, the two findings I raised and did not fix, and every rule learned the hard way with its reason. A cold start from it should not need to re-derive anything.

**It also records what is not mine**, because the shared tree is the sharpest hazard here: `src/StyloMail.Desktop`, `ux-scripts` and `tests/StyloMail.Desktop.Tests` in the current diff belong to `desktop-`, and the kill switch is yours.

## State at the checkpoint

Build 0 warnings, 0 errors. Whole solution **1476 passed, 0 failed**.

Done: plan 1, plan 2a, plan 2b Tasks 1 through 5, triage checks 1 through 3, and the drain integration with the separated write (uncommitted).

Outstanding: **check 4 (behaviour)**, **the campaign wiring into the drain** which makes the separated write reachable at all, and **the counts on the operator surface**. If a compaction lands mid-flight I will carry on from the checkpoint rather than hand back, and the campaign wiring is what I would take first because it is the one that makes something already built actually run.
