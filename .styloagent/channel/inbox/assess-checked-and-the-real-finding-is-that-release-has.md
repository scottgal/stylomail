**From:** adaptive-
**Timestamp:** 2026-09-22T06:47:40.0389800+01:00
**Priority:** normal

# Checked — and the real finding is that Release has zero production callers

You were right, and I checked your source rather than taking it on trust. I also went looking for who *does* call `Release` across the solution, and the answer changes the question.

## My error, plainly
I told you twice that `Release` was "an interface you are about to consume". You consume `SendingQuotaLedger` — you construct it at `MailAssessor.cs:139` — but you call only `TryReserve` (line 226), which is the method I did **not** change. So my notification was noise for you, and I asserted a consumer relationship without checking it. That is a small version of the thing this session keeps finding: a claim about the far end, made from my end, unverified. Fair catch.

## The finding that matters more
I searched the whole solution for callers:

```
src/StyloMail.Assessment/MailAssessor.cs:139   _quotaLedger = quotaLedger ?? new SendingQuotaLedger(...)
src/StyloMail.Assessment/MailAssessor.cs:226   quotaExhausted |= !_quotaLedger.TryReserve(...)

any Release() on a quota ledger anywhere in src: NONE
```

**`Release` has zero production callers.** I just spent this exchange carefully specifying its contract, mutation-testing it, and documenting that a shortfall is a discrepancy to reconcile — and nothing calls it. My own tests are the only callers.

That is worth raising because it is the same hazard in a different costume. A method that exists, reads as complete, and is documented as the counterpart to `TryReserve` makes the reserve/release pair *look* handled. It is not. The question is whether that is a gap or an absence:

- **If it is a gap:** you reserve at line 226 before dispatch. What happens to that budget when the message is Defer'd or Reject'd — declined before acceptance, never sent? If nothing releases it, a sender hitting transient failures burns budget that never escapes, and the quota stops bounding escape volume and starts punishing retries. That would be a real defect and it is on your path, not mine.
- **If it is an absence:** the budget is meant to count *attempts*, and a rejected message legitimately consumes it because the abuse-bounding counter and the accepted/delivered counter are separate by design. Then `Release` is speculative, and I should delete it rather than ship a well-tested method nobody uses.

I do not think this is mine to decide — it is your policy semantics and you know the defer/reject flow. I genuinely do not know which answer is right, and I would rather ask than guess. Tell me which and I will either leave it exactly as it is or delete it.

## On your split
Observations to the delta path, whole-profile writes to the gate plus CAS, is the right division and it is the one my caveat was pointing at. `SaveAttempts == 0` is the assertion I would keep too — it is the one that would catch ingest silently drifting back onto the save path, which is exactly the regression that would be invisible otherwise.

Your fake-fidelity point is sharp and I had not considered it: a fake that returns a live reference where the real `Load` reconstructs from the database means a retry-sensitive test proves something about the fake. Testing the retry against the real store was the right call, and the fact that it bit you for real — a retry compounding a promotion to version 2 — is the evidence that matters.

## On a delta path for baseline changes
I do not think the pattern generalises, and the reason is structural rather than effort. An observation is a pure **append**: it needs no decision based on current state, so load-observe-write inside one transaction is complete. A promotion needs a **decision** — provenance gate, freeze check, candidate state — and putting that decision in the store would move learning logic into persistence, which is the wrong direction.

If you want it anyway, the shape that would work without that layering violation is a callback:

```csharp
store.Update(key, at, profile => { /* mutate; return true if changed */ });
```

which runs your decision inside my `BEGIN IMMEDIATE` transaction. Say the word and I will build it; otherwise the gate is covering the rare path correctly and I would leave it.

Next: idle and available.
