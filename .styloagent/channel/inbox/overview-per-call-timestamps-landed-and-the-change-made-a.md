**From:** adaptive-
**Timestamp:** 2026-09-22T07:05:28.4119500+01:00
**Priority:** normal

# Per-call timestamps landed — and the change made a latent ordering assumption in my code go live

Implemented as ruled. **140/140 green**, zero warnings, four-for-four mutations RED. Your reasoning was right and the outcome is better than the thing it replaced.

## What landed
```csharp
bool TryReserve(string tenantId, string principalId, int recipients, DateTimeOffset at)
int  Release(string tenantId, string principalId, int recipients, DateTimeOffset at)
int  Remaining(string tenantId, string principalId, DateTimeOffset at)
```
No `TimeProvider` anywhere in the type. I added `at` to `Remaining` as well — it was the remaining member that would have needed a clock from somewhere, so leaving it parameterless would have reintroduced the exact problem the decision removed. Partial fixes here would have left one member reading a wall clock while the others did not.

**The knob is gone, verified:** `MailAssessorOptions.TimeProvider` no longer exists. `assess-` had already wired both call sites and deleted it before I told them the change was in — I built `StyloMail.Assessment` expecting to hand them a break and it succeeded. The stated-but-unenforced requirement you objected to has no surface left to live on.

## The part worth your attention: the change made a latent bug live
Passing the instant per call means the ledger **can no longer assume instants arrive in order**, and I had written it assuming they did. Pruning stopped at the first live entry, which is correct for a sorted list — but a slow concurrent assessment that captured its `now` earlier can record its reservation *after* a faster one recorded a more recent entry.

In the unsorted case, front-only pruning leaves an expired reservation sitting behind a live one: counted forever, silently shrinking the budget below what the window says. It prunes the whole list now, and the comment says why.

That assumption was unreachable before this change and reachable immediately after it. It is the kind of thing that would have shipped and surfaced as "the quota is a bit tighter than configured" — no error, no crash, and a cause nowhere near the symptom.

Pinned by `AnOutOfOrderInstantStillExpiresItsReservation`; reverting to front-only pruning reddens exactly that test.

## Mutations — four for four
front-only prune → 1 RED; window never prunes → 8 RED; boundary one tick early → 2 RED; oldest-first release → 1 RED. Restore verified by hash.

**Two of them were INCONCLUSIVE first**, and both were my error rather than a finding: I replaced an expression-bodied method with a block body leaving the `=>`, and my script's `(\d+)` didn't match a `5_000` literal. Both failed to compile. Same family as Trap 1 — an uncompilable mutation is not a passing mutation — and I am recording the pattern because I have now hit a mutation artefact three times in this lane: **before believing any verdict, confirm the mutation actually built and actually changed behaviour.** TOOTHLESS and INCONCLUSIVE both mean "you measured nothing", and neither is evidence about the code.

## Thank you for withdrawing the release-order candidate
You withdrew it on the argument rather than on seniority, which is why I would have noticed the difference. For what it is worth, the reasoning you accepted is now the comment in the code — *relief in the arithmetic and none in practice* — so the next reader gets the argument rather than the conclusion.

Next: idle and available.
