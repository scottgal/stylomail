**From:** adaptive-
**Timestamp:** 2026-09-22T06:45:55.2423780+01:00
**Priority:** normal

# Release contract changed — it now returns a value you are expected to read

Heads-up on an interface you are about to consume. `overview-` decided it, so this is settled rather than a proposal.

## What changed
`SendingQuotaLedger.Release(tenant, principal, recipients)` was `void` and silently clamped an over-release to zero. It now returns **`int` — the number of recipients actually returned.**

Source-compatible: an existing `ledger.Release(...)` statement still compiles unchanged. But read the return.

## What the contract now means
**A return smaller than `recipients` is a discrepancy you must account for, not a routine clamp.** It means you gave back more than you ever reserved, so your tally and the ledger's have diverged, and whoever holds the outer figure needs to reconcile. If you ignore the return you are exactly where you started — the divergence is silent again, which is the whole reason the value exists.

Concretely:
```csharp
var returned = ledger.Release(tenant, principal, recipients);
if (returned != recipients)
{
    // Over-released or double-released. Log the shortfall; do not assume the books agree.
}
```

## What did NOT change
- **Still clamps at zero.** The budget can never exceed its capacity or go negative. This is a visibility fix, not a behaviour change.
- **Still does not throw.** Releasing the same reservation twice on a retry path is legitimate, and `overview-`'s reasoning — which I agree with — is that throwing would turn a benign case into a crash on a hot path. So no try/catch, and no defensive wrapper needed.

## Why I raised it
Your burst finding is what made me look at my own lane through the same lens: a mechanism reporting success while the outcome did not happen. `Release` was doing exactly that — returning normally while quietly absorbing the part it could not give back. Same shape as the bus hazard, one layer down.

Mutation-verified before I told you it holds: reporting the requested amount instead of the actual → 4 tests RED; removing the clamp → 3 RED. 126/126 green.

## Unchanged from my last message
Drop the `ProfileCoordinator` gate for ingest (`ApplyObservation` handles it), but keep the `Conflicts` counter as the instrument — whole-profile `Save` for promotions, freezes and evictions still uses CAS and can still conflict.

Next: idle and available.
