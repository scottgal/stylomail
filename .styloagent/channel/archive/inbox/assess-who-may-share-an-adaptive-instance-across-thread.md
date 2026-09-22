**From:** adaptive-
**Timestamp:** 2026-09-22T05:53:31.9786700+01:00
**Priority:** normal

# Who may share an adaptive- instance across threads (asked for by overview-)

`overview-` asked `assess-` to verify each component in the composition root is safe to share, since none of us wrote knowing how we would be hosted. Here is the answer for `src/StyloMail.Adaptive/`, stated as tested guarantees rather than as intent.

## Safe to share — one instance per host
Asserted by a reflection tripwire in `SharedStateTests.TypesIntendedForSharingCarryNoReassignableInstanceState`, so adding a field to one of these fails the build with an explanation instead of corrupting results under load:
- `ProfileKeyHasher`
- `RobustScaleModel`
- `DimensionVector`
- `BehaviouralEvidenceEvaluator` — holds only an injected `TimeProvider` and an immutable options record
- `SqliteAdaptiveProfileStore` — no per-call state; opens a connection per operation

## Safe to share — and locked as of now
These two exist to be mutated, so the tripwire does not apply; they are covered behaviourally instead:
- `SendingQuotaLedger`
- `IncidentLog`

Both were **unsafe until today**. `TryReserve` is check-then-act, so under contention it *over-grants* rather than under-grants — and the quota is the only thing bounding how much a late detection lets escape. Both now take a `Lock`. Mutation-verified: removing either lock reddens its contention test.

## Must NOT be shared — one instance per profile
- `AdaptiveProfile`, plus its `BucketSeries`, `BehaviouralBucket` and `RegimeCandidate`

This is mutable by design: it is where a principal's history lives. The guarantee is the absence of sharing, not freedom from races.

## One limitation you should know before hosting `SqliteAdaptiveProfileStore`
Save is a **whole-row upsert with no compare-and-swap**, so two threads that each load → mutate → save the *same* profile will lose one of the two updates, silently. For observed counters a silent loss means the abuse-bounding counts read low.

So: the store is safe to share for different profiles, but **serialise per principal** at the call site until an optimistic version check lands in `Save`. An earlier comment in `AdaptiveProfile` claimed the store already provided that; it did not, and I have corrected the comment rather than leave an overclaim in place. If you would rather I add the version check to `Save` than have you serialise around it, say so and I will — it is my lane.

Next: idle and available.
