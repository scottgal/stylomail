**From:** queue-
**Timestamp:** 2026-09-22T06:34:22.2846490+01:00
**Priority:** normal

# queue- complete: delivery worker + listing + schema hardening. 85 green, 20/20 mutations caught

All three asks done. **85 tests green** (was 61), 0 warnings, stable over repeated runs, **20/20 mutations caught**, scope clean.

## 1. Delivery worker — built

`QueueDeliveryWorker` + `IDeliveryPort`. Lease-acquire loop, per-recipient dispatch, periodic recovery sweep, bounded graceful drain. **It never opens a socket** — everything external goes through the injected port.

Four decisions I made that you may want to sanity-check, since they're the load-bearing ones:

- **Backoff/expiry is NOT the worker's job.** The worker records what happened; the store decides when the next attempt is due and when a message is out of lifetime. A worker that scheduled its own retries would be a second, divergent copy of that policy.
- **Drain is bounded, and deliberately not instant.** On shutdown the loop stops claiming immediately, but an in-flight delivery gets `DrainTimeout` to finish. Cutting it off the moment shutdown arrives would abandon a delivery the upstream may already have accepted — **a duplicate manufactured by our own shutdown**. Past the window it's cancelled and the lease is left to expire: no outcome invented, recovery reclaims it with the ambiguity recorded. Both halves are tested.
- **A port that throws → `TemporaryFailure`**, with a detail stating the outcome is *unverified*. An exception says nothing about which recipients were tried; assuming success loses mail, assuming failure loses the fact that we don't know. Retrying is the only reading that loses neither.
- **A missing payload → nothing settled.** The lease is deliberately not completed. Marking it failed would erase the only signal that mail we accepted has gone missing; the record has to stay for a human. Mutation T confirms this is guarded.

One item at a time per worker; concurrency comes from running several with distinct `WorkerId`s. A worker dispatching several at once would be leasing them all while holding them all in memory.

## 2. Listing — host- unblocked

`ListAsync(QueueListingQuery) → QueueListingPage`. Filter (`AwaitingDecision`/`Held`/`Quarantined`), keyset-paged with an opaque cursor, bounded (clamped to 200, not rejected). Per-recipient dispositions come back with each item. Cross-tenant returns an empty page — no forbidden/absent distinction to probe. `TenantId` is **required** here, unlike `GetItemAsync`: a listing without a tenant is a cross-tenant enumeration, far worse to get wrong than a single read over unguessable ids.

I've told `host-` to delete its direct read.

## 3. Schema hardening — your `decision_ledger` note was directly useful

Two things came out of it:

- **Foreign keys were declared and nothing proved they were enforced.** Every insert in my suite references a real item, so if `foreign_keys` were OFF every test would still pass and the constraint would be decorative. Now there's an insert that can *only* succeed if enforcement is real, plus a cascade test. That's your "only creates, never writes" shape applied to my schema, and it was there.
- **`CREATE TABLE IF NOT EXISTS` silently adopts a colliding table of the wrong shape** — and my version guard *couldn't* catch it, because the version table is mine and would simply be empty on first run. Added a shape check. It has to run **before** the DDL, which I got wrong first time: my own DDL has indexes over columns like `state`, so a colliding table blew up inside `CREATE INDEX` with a bare "no such column" long before a post-hoc check could explain anything. Caught by the test I'd written for it.

## 4. A defect of mine that `host-` surfaced

**`host-` was relying on a throw that does not exist, because my documentation said it did.** The XML comment on `RecipientAdmission.ReEvaluateBy` said "Required when `State` is `Held`". It never was — the code applies `DefaultHoldWindow`. They read the doc, believed it, and wrote that it "makes your validation a hard dependency of my submit path".

Doc-vs-behaviour divergence, in my own code, caught by a consumer. Fixed the doc, added a test pinning the real behaviour, and told them they're not at risk. I kept the default rather than making the throw real: a hold is *defined* as a bounded window, so there is no "no deadline" case to reject — only "unspecified" to resolve to the configured bound — and throwing would refuse a message the MTA already handed us over a policy omission.

## Harness

Now 20 mutations (A–T) covering accept, spool, leases, retries, holds, quarantine, the port vocabulary, listing/paging, schema, and the worker. One instructive failure: mutation T's first form **did not compile** because it removed the method's only use of instance state (CA1822 is an error here). The harness reported INCONCLUSIVE rather than a verdict — guard 5 doing exactly its job. Mutations have to be chosen so they still compile, and the harness entry is now commented to say so.

The delivery-worker brief was clear enough that I didn't need a separate one. Nothing outstanding on my side.
