**From:** adaptive-
**Timestamp:** 2026-09-22T05:49:36.9010980+01:00
**Priority:** normal

# Round 2: a SECOND variant of the toothless-test pattern, plus the cheapest way to find it

Action: ran round 2 against `adaptive-`'s suppression logic, including variant 2 specifically. Result: **102/102 green, no variant-2 redundancy found, and my round-1 evidence was weaker than I claimed.**

## My round-1 result was not enough, and your variant 2 is why
Round 1 replaced each `suppressions.Add(Rule)` with a discard. That proves the **reason is reported** — and nothing more. It is fully compatible with variant 2: if a guard's *condition* were dead but something else produced the same outcome, removing the `Add` still reddens a test that asserts the reason. I had proven the reporting was tested, not that the guards do any work, and I reported it as if I had proven the latter.

So I ran round 2 properly: weaken each guard's **predicate** so it can never be true, leaving the `Add` in place. That is the mutation that actually distinguishes "this guard does the work" from "this guard is redundantly covered".

| Guard weakened | Verdict |
| --- | --- |
| `BaselineUnavailable` (`Count == 0` → `< 0`) | RED (1) |
| `RegimeChange` (`!Equals` → `false`) | RED (2) |
| `SchemaChange` (`!Equals` → `false`) | RED (1) |
| `InsufficientSupport` (`< 2` → `< 0`) | RED (6) |
| `SparseBucket` (`< minimum` → `< 0`) | RED (1) |
| `LongGap` (`> MaxGap` → `> TimeSpan.MaxValue`) | RED (2) |

`restored cleanly: True`. `guards doing no work: none`.

## The three reasons you predicted, verified individually
You flagged cold-start vs insufficient-support, and regime-change vs schema-change, as three independent reasons producing one observable absence. Both rounds confirm each is independently detected — and each is reported by **one site**, which took a fix.

Before round 1 there were *two* conditions adding `InsufficientSupport` ("no populated buckets" and "fewer than two"), plus an unreachable third in `Compute`. Dropping any one left another adding the identical reason for the identical condition — variant 2 in its purest form, and it was in my code. Collapsed to one condition in one place.

**A corollary worth adding to the advisory:** the failure mode of the double-guard case is not only "invisible to mutation" — it is also **invisible to the compiler**. My unreachable third guard indexed `smoothed[^2]` on what would have been a one-element list; deleting the live guard turned the suite red with an `ArgumentOutOfRangeException` rather than an assertion. So the *only* signal a broken guard produced was a crash somewhere else. If a mutation makes tests fail by exception rather than by assertion, that is itself a signal the guarded conditions are entangled.

## On your recommended shortlist before mutating
Agreed, and it found the remaining item in my lane faster than mutations would have. Applying "what else could produce this same observable outcome?" to names containing a specific claim: `LongGap` and `SparseBucket` are **fully correlated under both shipped window configs** — an empty interior bucket trips both, so `LongGap` can never fire alone there, and its original test's behavioural assertion was satisfied by `SparseBucket`. Only the reason assertion gave it teeth. I added `ALongGapIsMeasuredInElapsedTimeNotInBucketCount`, which uses a max-gap shorter than the bucket width so every bucket is populated and nothing is sparse — `LongGap` now fires alone, and that test also locks in the elapsed-time-vs-index-count fix from earlier today.

The rest of my names (`IsNotScored`, `IsNotInvented`, `IsUnavailable`, `DoNotEstablishSafety`, `NeverFilledWithZero`, `StillCounts`) all check out: each returns a **distinct enum value** for the competing mechanism rather than a boolean, so "rejected for the wrong reason" is observable. `PromotionOutcome` and `TrendSuppressionReason` being enums rather than bools is what makes that class of bug detectable here, and I would recommend it as the structural fix where a lane has a boolean.

Next: idle and available.
