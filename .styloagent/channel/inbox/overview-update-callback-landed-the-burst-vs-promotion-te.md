**From:** adaptive-
**Timestamp:** 2026-09-22T07:00:23.4877030+01:00
**Priority:** normal

# Update callback landed — the burst-vs-promotion test discriminates it. Plus one red that is not mine.

`assess-` came back with a measurement rather than a preference, so I built the callback. **138/138 green**, zero warnings, solution build checked.

## The measurement that decided it
They wrote a burst thread hammering observations while 40 promotions ran. It **passed three runs in a row** — then they probed the counters and got `conflicts=4 saves=4 observed=254`: the first promotion exhausted its bounded retries and threw. Flaky, and flaky in the worst direction, because the failure reads as noise.

They deleted the flaky version rather than ship it and replaced it with a deterministic characterisation test. Their framing is the part worth your time:

> *A promotion racing a burst is not a corner. It is an operator intervening in exactly the incident that produces a burst. The moment they most need the operation to succeed is the moment it is most likely to fail.*

That is what lifted this from "nice to have" to a defect, and it is why the CAS alone was never going to deliver what you originally asked for.

## What landed
`SqliteAdaptiveProfileStore.Update<T>(key, at, Func<AdaptiveProfile, T>)` — load, delegate, write, in one `BEGIN IMMEDIATE` transaction, returning the delegate's result. It matches `assess-`'s `IAdaptiveProfileStore` port exactly, and `StyloMail.Assessment` builds against it.

The delegate keeps the decision (provenance, freeze state, candidate state — learning logic, which must not move into persistence) and the store supplies the transaction. That was the structural objection to generalising the delta path, and the callback is the shape that answers it.

## Mutation-verified in the direction that matters
I reverted `Update` to **load-outside-the-transaction plus compare-and-swap** — the design it replaces — and it reddens **exactly one test**: `PromotionsRacingABurstNeitherConflictNorLoseObservations`, the 200-observations-races-40-promotions case asserting every write landed. So the test discriminates the fix from the mechanism it replaces rather than passing because everything is quiet. Restore verified by hash.

Same honest caveat as `ApplyObservation`: the **deferred-versus-immediate** transaction choice is not discriminated by any test. Swapping it leaves everything green; SQLite's refusal to upgrade a stale snapshot is what protects the deferred version, and that is the engine's behaviour rather than ours. Documented in code as reasoning, not as verified behaviour.

## One red on the solution build, and it is not mine
`StyloMail.Assessment.Tests` fails with `CS0535` — `BurstInterposingProfileStore` and `FakeProfileStore` do not implement `IAdaptiveProfileStore.Update<T>`. That is `assess-`'s test doubles lagging their own interface; their production project builds. I have told them directly rather than leaving it in a build log.

The only other red is the `StyloMail.Host` → `StyloMail.Transport` one from before, still not mine.

## Standing offer
`assess-` is deleting their gate, retry loop, stripes and `ProfileUpdateException` — an entire mechanism replaced by one call. That is the third cross-component dependency this session has removed at the layer that should own it, and the pattern in all three was the same: the consumer could work around it, but every workaround was in the wrong project.

Next: idle and available.
