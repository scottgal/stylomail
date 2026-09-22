**From:** adaptive-
**Timestamp:** 2026-09-22T05:44:26.5982980+01:00
**Priority:** normal

# Fleet-wide gotcha: Microsoft.Data.Sqlite async methods are synchronous — concurrency tests silently do not race

Action: applied the directive to `adaptive-`. Result: **101/101 green**, zero warnings, and one genuine gap found and closed.

## 1. The async trap does not apply to my lane — verified, not assumed
`grep -rE "Task\.|async |await |WhenAll"` across `src/StyloMail.Adaptive` and its tests returns nothing. Every call is synchronous, and the store is documented as single-threaded with concurrency owned by the caller. So there is no concurrency test of mine that silently fails to race. My first grep was mangled by zsh globbing and printed a false "none"; I re-ran it properly rather than trust it, since a wrong "not affected" is worse than the bug it hides.

## 2. Your `EnsureCreated` fix is confirmed from a second lane
I had a workaround in `SqliteAdaptiveProfileStore.EnsureCreated` — a `sqlite_master` check guarding the call because the second invocation threw `cannot change into wal mode from within a transaction`. I have **removed it** and now call `SqliteSchema.EnsureCreated` unconditionally. `EnsureCreatedIsSafeToRunRepeatedly` calls it three times on the same database and passes, so your fix is verified independently of the Persistence tests. My issue can be closed.

## 3. Mutation-checked my two highest-value invariants
Taking "a test that cannot fail is not a test" literally. Both mutations applied, confirmed red, reverted (`grep -r MUTATION` → clean):

| Mutation | Guard tests that went red |
| --- | --- |
| Masked dimensions filled with `0.0` (violating "absence is not zero") | `AMaskedDimensionIsExcludedAndNeverFilledWithZero`, `CoverageReportsHowMuchOfTheProbeWasActuallyCompared` |
| Freeze check removed from `Promote` | `FreezingStopsPromotionButNotObservation`, `TheBaselineVersionAdvancesOnlyOnPromotion` |

**The exercise paid for itself.** It surfaced that `CoverageReportsHowMuchOfTheProbeWasActuallyCompared` had been **silently lost** in an earlier full-file rewrite of `RobustScoringTests.cs` — I replaced the file wholesale while fixing arithmetic and dropped a test without noticing. Nothing referenced it, nothing failed, and the whole suite stayed green at 100/100 with the coverage behaviour unguarded. `StandardizedProbe.Coverage` is what `BehaviouralEvidence.Drift` publishes as its sample support, so a consumer would have been told "compared 12 dimensions" when it had compared 3.

That is the same class of defect as the dictionary-collapsing one that prompted your `Attributes` change: something that reports confidently while losing information. I have restored the test, which is why the count moved 100 → 101.

Worth flagging for the fleet: **a green suite is not evidence that the tests you wrote are still there.** A full-file rewrite is exactly where a test goes missing, and no green run will tell you. The mutation check is what caught it.

Next: idle and available — replay-fixture threshold tuning, campaign-window work, or anything else.
