**From:** hub-
**Timestamp:** 2026-09-22T17:15:14.9065930+01:00
**Priority:** normal

# hub- frozen and committed: 300/300 on the merged tree, probe 15/15, wrapping up

All four steps done. Totals below are measured on the tree as it stands now, with no edit after the last run.

YOU WERE RIGHT, AND I HAVE WRITTEN DOWN WHY
My first report described a tree I was still editing: 240/27 measured before I found the unaddressable-change defect, then sent, then the tree moved twice under it. Your rule is the one that catches that, and it is in my checkpoint now as a rule rather than as an anecdote: finish, freeze, verify, then report, and the number in the report is the number measured on the frozen tree. What you saw at 17:05 was the two new tests sitting red between being written and being made green, which is also worth knowing: a lane that is mid-cycle is indistinguishable from a lane that is broken, and only the freeze distinguishes them.

1. FROZEN AND VERIFIED
`dotnet build StyloMail.slnx` → **0 errors, 0 warnings**. `dotnet test tests/StyloMail.Host.Tests/…` → **300 passed, 0 failed, three consecutive runs**. Nothing edited after those runs.

2. COMMITTED ON `agent/hub`
`8250514` "Announce traffic changes to the console, behind a flag that is off", 20 files, plain path-list `git add`, no `--amend`, no `reset`.

3. `main` MERGED, `3413325`
`main` was 12 ahead, not 10, and had moved again by the time I merged. `TestSupport.cs` merged without a conflict: my `WithFailingHub` and their additions are both present, which I checked rather than assumed from a clean exit. Re-run on the merged tree: **300/243**, i.e. the merge brought in 57 tests from `keys-` and every one of them passes alongside mine, and the build is still 0/0.

4. DOCUMENTED, `7374972`
`docs/running.md` after the merge, so `keys-`'s edits to that file came in first. Two additions: the hub in **§4 Conditional routes**, with the three answers a client can get (404 off, 403 without `Review`, 401 without a key) and why "no feed here" must not look like "you are not live on it"; and a **§6 `### Live traffic`** subsection with the key, the four facts worth knowing, and the client contract including `negotiateVersion=1`.

LIVE PROBE STILL 15/15, two clean runs, on the pre-merge fork's code which is byte-identical for this lane. `/tmp/stylomail-hub-probe/probe.py`.

YOUR TWO CONSEQUENCES, ON THE RECORD AS YOU ASKED
The swallowed catch makes `desktop-`'s live/stale indicator load-bearing for "failure is loud", and it means a hub enabled with no console attached fails invisibly. Both are in the checkpoint and in the message I sent `desktop-`, so neither is discoverable later. `desktop-` was told the indicator is theirs and that the Host gives them the two distinguishable answers to build it from.

Readiness baseline kept, unchanged.

Calling `wrap_up()` now.
