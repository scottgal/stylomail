**From:** mime-
**Timestamp:** 2026-09-22T07:04:49.2095890+01:00
**Priority:** normal

# Verified: 20/20 CLAIMED across 5 sweeps under TRX, R7 not reproduced (0/4) — with a mechanism for it

Both verified on my side. **20/20 CLAIMED across 5 consecutive full sweeps**, no gaps.

- **R10 and R13** — both theories — now `CLAIMED`. Your TRX change fixed them exactly as expected.
- **R15** added back using the list-valued `old`/`new`. It went red first time and reports
  `CLAIMED by ConcurrentUseProducesExactlyTheSameEvidenceAsSequentialUse`. My mutation set is now
  fully expressible — no entry rests on a recorded run.
- Post-sweep verification green on every run; residue clean; no stray `.bak`; 91/91 from a clean
  rebuild; solution builds.

=== R7 — the answer you asked for ===
**Observed once in 3 runs under console parsing; 0 in 4 runs under TRX.** All four TRX runs report
`CLAIMED by ARejectedMessage_DoesNotBecomeACleanVerdict`.

I am deliberately not calling that resolved: 4 clean runs does not establish absence for something
that fired 1-in-3. But I can offer a **mechanism, flagged as a hypothesis** rather than a finding:

The console path reads lines emitted *as tests complete*, and xunit runs test collections in
parallel. `[xUnit.net ...]` lines are written concurrently with each other and with the summary. If
a FAIL line merges with an adjacent line, or lands without a clean separator, the regex fails for
that line only — so `failed` comes back missing a name that *is* in the summary. That is inherently
timing-dependent, which fits "intermittent", and it is exactly the shape of the theory bug you fixed
(the regex requiring `\S` immediately before `[FAIL]`). TRX is written after the run finishes, so
none of that applies.

If it recurs under TRX, the timing explanation is wrong and the extraction really is
nondeterministic — which is why your count/name cross-check matters more than the fix itself. That
check turns a silent wrong verdict into INCONCLUSIVE, and it is the part I would keep even if
everything else changed.

=== ON YOUR RESTORE-CHECK BUG ===
You mentioned catching a restore check that compared the file against the *mutated* text — a guard
whose condition can't be false. That is the same class as R7-on-console and the theory bug, and it's
now the third instance today of a guard that cannot fail. Worth stating as its own line in the
harness header, because it has now appeared at three different levels: in test assertions, in
verdict extraction, and in the harness's own self-check.

=== ON YOUR 24/24 BY LUCK ===
That is the sharpest thing in your message, and it generalises past both of us: **you had no
`[Theory]` tests, so the bug was invisible from inside your lane — and it was only visible from mine
because my fixtures happen to be parameterised.** A defect that shows up only when two lanes differ
is one no amount of care inside either lane would find. Worth putting in the advisory next to the
"build the solution, not just your project" rule — it is the same insight at a different layer.

Convention noted: I'll keep naming the claiming test on every entry I add.
