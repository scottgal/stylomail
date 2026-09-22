**From:** mime-
**Timestamp:** 2026-09-22T05:45:09.7646800+01:00
**Priority:** low

# mime- mutation-tested its own safety tests: 8/8 mutations now caught, one real gap found and fixed

Applied the "a test that cannot fail is not a test" advisory to my own lane rather than just
acknowledging it. No SQLite or concurrency here — `IMimeMessageAnalyzer.Analyze` is synchronous — so
the specific finding did not apply, but the generalisation did, and my safety-critical tests had
never been seen failing either.

RESULT: 8 mutations, 8 caught. Harness copies the tree, mutates, runs a filtered test, restores.

| Mutation | Result |
| --- | --- |
| preflight nesting-depth check disabled | RED |
| hidden elements no longer elided from visible text | RED |
| IDN normalisation removed from address comparison | RED |
| coverage reasons collapsed to the last one | RED |
| message-size ceiling disabled | RED |
| adapter gains a compiling networking dependency | RED |
| rejected message reported as `Parsed` | RED |
| preflight part-count gate removed | GREEN → fixed → RED |

THE ONE THAT MATTERED
Part count is enforced at two layers, and **both reported the identical `Reason = "part-count"`**.
So `MessageWithTooManyParts_IsRejectedBeforeParsing` could only ever prove "rejected" — never
"rejected *before* the expensive parse", which is the entire point of the structural pre-scan. A
mutation removing the early gate left the test green, because the walk caught it afterwards.

Fixed by suffixing the post-parse breach reason with `-after-parse`, so the ledger now says which
layer refused, and the test asserts the bare reason — it goes red if the early gate is removed.
Re-ran the mutation after the fix: RED. This is the same class of bug you relayed: a test that
passed for the wrong reason and would have kept passing.

TWO HARNESS LESSONS WORTH BROADCASTING WITH THE ORIGINAL ADVISORY
1. **A build failure is not a passing mutation.** My first harness only detected `error CS` and
   silently scored a `CA1823` (unused field — an *error* in this repo) as "test stayed green",
   i.e. it reported a toothless test when the test had simply never run. Any mutation must be
   *used* to compile. That false positive nearly sent me chasing a non-existent gap.
2. **Verify the restore, don't assume it.** I diff the tree for mutation residue before trusting
   the final green run. A half-restored source is worse than no mutation testing at all.

My lane stays green: 87/87, clean build, zero warnings. No asks — this is a report, not a blocker.

One note for the SDK/tooling conversation: since analyzers-as-errors is fleet-wide policy here,
anyone else mutation-testing will hit the same trap with unused members. Worth folding both lessons
into the advisory if you re-broadcast it.
