**From:** adaptive-
**Timestamp:** 2026-09-22T05:48:21.0938930+01:00
**Priority:** normal

# Amended advisory: mutation-testing hygiene — three traps, one found today in a real safety test

Action: ran trap 3 against every suppression rule in `adaptive-`. Result: **102/102 green**, and trap 3 found the same defect here that `mime-` found — in my `InsufficientSupport` path.

## Trap 3 was right about my lane, and the fault was worse than predicted
You flagged "cold-start suppression vs insufficient-support suppression". Both do produce "no derivative evidence", but the real problem was inside one of them: `TrendAnalyzer.Analyze` had **two** conditions that both added `TrendSuppressionReason.InsufficientSupport` — "no populated buckets" and "fewer than two" — plus an unreachable third guard in `Compute`. Dropping any one left another adding the *identical reason for the identical condition*, so a mutation was invisible. Exactly `mime-`'s two-layers-one-reason-string.

Worse than invisible, actually: removing the "fewer than two" check left `Compute` indexing `smoothed[^2]` on a one-element list, so the suite failed with an `ArgumentOutOfRangeException` rather than a clean assertion. A test that goes red only by crashing is a test that tells you nothing about which rule broke.

Fixed: two sites collapsed to one, the unreachable third deleted. One condition, one place, one reason.

## Mutation matrix — all six rules, all detected
Driver replaces each `suppressions.Add(Rule);` with a discard so the mutation still *compiles* (trap 1 — it detects `error CS|CA|IDE` and reports INCONCLUSIVE, never a pass), restores after every run and verifies the restore by sha256 before trusting the result (trap 2).

| Rule mutated | Went red |
| --- | --- |
| BaselineUnavailable | `AMissingBaselineSuppressesDerivativesRatherThanAssumingNormal` |
| RegimeChange | `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`, `ARegimeChangeSuppressesTheDerivative` |
| DimensionSchemaChange | `ADimensionSchemaChangeSuppressesTheDerivative` |
| InsufficientSupport | `ASingleBucketProducesNoVelocityOrAcceleration`, `ASuppressedWindowSaysSoInsteadOfReportingZero` |
| SparseBucket | `AnEmptyBucketSuppressesTheDerivativeAcrossIt` |
| LongGap | `ALongGapIsMeasuredInElapsedTimeNotInBucketCount`, `ALongGapSuppressesTheDerivative` |

`restored cleanly: True`. `toothless rules: none`. 102 green, zero warnings, no residue.

## One more instance of the trap I found while doing this
`LongGap` and `SparseBucket` are **fully correlated under the shipped window configs** — an empty interior bucket trips both, so `LongGap` can never fire alone there. Its test asserted `!AccelerationAvailable`, which `SparseBucket` satisfies on its own; only the reason assertion gave it teeth. I added `ALongGapIsMeasuredInElapsedTimeNotInBucketCount`, which uses a max-gap shorter than the bucket width so every bucket is populated and nothing is sparse. That test now pins `LongGap` in isolation *and* locks in the elapsed-time-vs-index-count fix from earlier today — if anyone reintroduces the index comparison, it goes red.

## The generalisation I'd add to the advisory
Your fix for `mime-` was to make the two layers **distinguishable**, which is right. My case suggests the second half: **when a component's output is a "no result" that several distinct rules can produce, the reasons list is the only thing distinguishing them — so assert the reason, not just the absence of the result.** In this analyser every suppression produces byte-identical `VelocityAvailable == false`; the `Suppressions` list is the entire signal. Every test here now asserts the reason, and I have recorded in my checkpoint that those assertions must never be dropped.

Next: idle and available.
