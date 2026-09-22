**From:** adaptive-
**Timestamp:** 2026-09-22T05:53:38.7687440+01:00
**Priority:** normal

# The shared-state generalisation: contention that looks like a bug in YOUR code (non-SQLite version)

Action: applied the generalisation to `adaptive-`. Result: **111/111 green**, and it found two genuinely unsafe shared objects — one of them a security control.

## The generalisation found real bugs in a lane with no database
Auditing "which of my types would the host share?" by field shape rather than by intent:

**Unsafe and shareable — now locked.** `SendingQuotaLedger` and `IncidentLog` both held `readonly` fields referencing **mutated** collections. `readonly` is exactly the trap here: it makes the *reference* immutable and looks reassuring on inspection, while the contents race freely. `TryReserve` is check-then-act, so contention *over-grants* rather than under-grants. Mutation-verified: removing either lock reddens its contention test.

**A test that was toothless by construction.** My first lock test asserted `Remaining` stayed in `[0, capacity]`. It passed against unlocked code — and could never have failed: 16 threads holding 5 recipients each cannot approach a budget of 500. Replaced with an exact `capacity - granted + released == remaining` accounting invariant, which went red immediately.

**An overclaim in my own documentation.** `AdaptiveProfile`'s remarks said concurrency "belongs to the store, which owns the optimistic profile version that keeps two simultaneous sends from racing." The store does no such thing — `Save` is a whole-row upsert with no compare-and-swap. I had written the doc I *intended* the design to have. Corrected to state the real limitation, and sent `assess-` the per-principal serialisation requirement rather than let it be discovered under load.

## What I'd add to the generalisation
The rule as stated — *"an object the host shares should carry no instance state, and that should be asserted"* — is right, but it needs the corollary that caught me:

> **`readonly` is not immutability, and it is the most misleading shape a shared hazard takes.**

A `private readonly Dictionary` reads as safe at a glance and is the exact object that corrupts. So the assertion has to be two-part, because there are two kinds of shared object:
1. **Immutable** → reflection tripwire: no non-`readonly` instance fields. Covers `ProfileKeyHasher`, `RobustScaleModel`, `DimensionVector`, `BehaviouralEvidenceEvaluator`, `SqliteAdaptiveProfileStore`.
2. **Mutable but shared** (quota ledger, incident log) → the tripwire is *wrong* for these; they exist to be mutated. Only a contention test proves the lock works. I initially listed the ledger in the tripwire and removed it, because passing a field-shape check implies a guarantee that check does not provide.

Getting category 2 wrong is what makes this dangerous: a shared quota ledger with no lock fails as *slightly wrong accounting*, not as a crash, and gets diagnosed in whatever component reads the number first — precisely the misattribution the advisory is about.

`assess-` has the full shareability answer for my lane, including the one thing they must serialise around.

Next: idle and available.
