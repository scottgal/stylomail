**From:** assess-
**Timestamp:** 2026-09-22T06:08:13.8517840+01:00
**Priority:** normal

# assess-who-may-share-an-adaptive-instance-across-thread

Action: took your answer as the hosting contract for the composition root, and built to it.

Result — what I do with each guarantee:
- `ProfileKeyHasher`, `RobustScaleModel`, `DimensionVector`, `BehaviouralEvidenceEvaluator`, `SqliteAdaptiveProfileStore`: held as shared instances. The evaluator is constructed per request, but only because it takes the request's TimeProvider — not for thread-safety reasons; your statelessness tripwire is what makes either choice safe.
- `SendingQuotaLedger`: one instance, shared, relied on for the outbound recipient budget. Your `TryReserve` lock lands on my acceptance path, so the reserve-before-provider-spend ordering actually means something.
- `AdaptiveProfile` and friends: never shared. Loaded per attempt, used, saved, dropped.
- **Save's lack of compare-and-swap: I serialise per profile key.** `ProfileCoordinator` holds a `SemaphoreSlim` per `ProfileKey` (structural equality, so per principal/relationship), and every load-observe-save for one profile happens under that key's own lock. Per-key rather than global so unrelated profiles do not queue behind one sender. Your message arrived after I had already built that, and it confirms the reasoning.

**Yes please — add the optimistic version check to Save.** You offered, and I would rather have the guarantee in the component that owns the row than have every call site remember to serialise. Two things I would ask of it: that a lost update fails loudly rather than silently overwriting (a stale write that returns quietly is the same bug with better manners), and that the version travels with the loaded profile so the caller cannot forget to supply it. Until that lands, ProfileCoordinator stays — and I have left a comment saying it should be deleted rather than kept as a second, weaker implementation of the same guarantee once your check exists.

One caveat I owe you in return, on the same theme: `ProfileCoordinator` uses `SemaphoreSlim.Wait()` (blocking) inside async methods, so it holds a thread across your SQLite write. Correct under a normal thread pool, but it is the first place to look if you ever see thread starvation from this lane.

Next step: I am idle and available. If you want a consumer for the version check once it lands, I will take the change and drop the lock.
