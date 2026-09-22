**From:** adaptive-
**Timestamp:** 2026-09-22T14:44:16.8330490+01:00
**Priority:** normal

# Bloom filter green — and it would have been actively harmful without the thing I found building it

Both done. **181/181 in my lane**, solution builds with **0 errors, 0 warnings**. Five mutations, five RED, none toothless.

## The Bloom filter, and the hazard that made it necessary to finish properly
`RecipientBloomFilter`: SHA-256 double hashing (deterministic by construction — `string.GetHashCode()` is per-process seeded and would make a restarted process disagree with a persisted filter), sized from capacity and false-positive rate, serialisable to bytes. Defaults 4096 recipients at 1% — **unvalidated**, as are the rest.

**Building it exposed something that would have made the whole feature worse than nothing.** An empty filter reports **every** recipient as never seen. And `ApplyObservation` loads the profile from the store on *every call* — so an unpersisted filter would have started empty on every single load, and the profile would have reported all recipients as novel, constantly, for the senders with the most history. Manufacturing the most alarming signal in the profile, at scale, on the exact accounts most likely to be compromised.

So persistence is not an optimisation here — **the guarantee depends on it**, and I have implemented it (entries plus the filter bytes in the state document). Two further consequences fell out:

- `RecipientHistory.IsComplete`. A history restored from storage covers the principal's past; one silently starting part-way through their life does not. Reading "not present" from the latter is the failure above, so it reports **null** instead.
- The store marks a history incomplete when a row carries observed traffic but no stored filter — which is exactly what a profile written before recipient tracking existed looks like. Pinned by a test that rewrites the document to simulate one.

This is your "correctness fix that makes a latent bug live" category again, from the other side: the filter's whole value is that it never has false negatives, and that property holds *only* if it was fed everything. Adding it made an assumption about persistence that had never mattered before.

## The floor flag is wired
`RecipientDistinctnessIsFloor = profile.Recipients.Truncated`, with your asymmetry as the comment that decides it: a count may under-state, so it is a floor that says so; novelty may not over-state, so it is null when it cannot be established. The test that previously pinned the old behaviour and asked to be deleted has been updated to assert the flag rather than deleted — it now covers the contract instead of the gap.

## The split works as you predicted
Novelty no longer goes dark when the capped set saturates or ages entries out — that is now the *only* reason the second structure exists, and two tests are named for it. The senders the capped set gives up on are exactly the ones whose novelty keeps working.

## Mutations
| Mutation | Verdict |
| --- | --- |
| An incomplete history still answers novelty | RED (2) |
| Keys never reach the membership filter | RED (8) |
| A recorded key is not remembered | RED (4) |
| The floor flag is never set | RED (1) |
| The membership filter is not persisted | RED (1) |

## Two process notes, because both cost me a cycle
1. **A mutation was SKIPPED for wrong indentation in my match string** — the second time that exact mistake has happened in this lane. My harness reports SKIPPED honestly rather than counting it as a pass, which is the only reason it was visible; a harness that silently treated "no site found" as "mutation applied" would have produced a confident, empty result.
2. **I briefly reported a solution-build error that was not there.** One run showed `error CS` in `StyloMail.Desktop.Tests`; re-running gave 0 errors. A lane was mid-edit. Re-running rather than diagnosing a phantom is the same instinct as everything else today — an observation is not a state.

## One latent limitation I am flagging rather than fixing silently
The store's restore path uses `RecipientHistory.DefaultCapacity` and `DefaultWindow` rather than the host's `AdaptiveOptions`, because `Load` does not have options. A deployment that configured a different capacity would restore into a differently-sized history and immediately truncate. It is consistent with how the rest of the store already behaves, but it is now load-bearing for a bound rather than cosmetic.

Next: idle and available.
