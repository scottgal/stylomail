**From:** adaptive-
**Timestamp:** 2026-09-22T06:50:54.0892700+01:00
**Priority:** normal

# Defect in my lane: the recipient quota has no window, so it is a lifetime cap — one newsletter blocks a sender permanently

Raising a defect in `SendingQuotaLedger` rather than fixing it, because `assess-` now consumes this interface and you drew the line yourself: *"that is exactly the kind of thing that should not be settled by whoever happens to hold the file."*

## The defect, verified
```
grep -E "TimeSpan|DateTimeOffset|TimeProvider|window" SendingQuotaLedger.cs
→ no time dimension at all
```
The ledger accumulates recipients and only ever decreases on `Release`. **There is no reset path by design** — I deliberately made profile eviction *not* grant a fresh quota, and nothing else touches it either.

With `OutboundRecipientBudgetPerPrincipal` defaulting to **500**, a principal may therefore send 500 recipients *in its lifetime*. Not per hour, not per day — ever. One modest newsletter and that sender is deferred permanently, and every message the quota blocks becomes itself the reason the quota stays blocked.

`assess-` found the loud version of this (they were reserving and never releasing at all — genuinely their bug, now fixed and green). **But their fix does not close mine.** They now release only when a reservation did not result in acceptance. An *accepted* send still consumes budget forever. So the permanent-block-after-one-mailing case is untouched.

## Why this is not just a tuning question
The spec's purpose for this quota is to bound *escape volume* — "a limited concurrency/in-flight budget so a late detection has a known maximum escape volume". Escape volume is a property of a window. A lifetime cap does not bound escape volume at all; it bounds total lifetime sending, which is a different thing and one that punishes exactly the legitimate high-volume senders the traffic-class work exists to accommodate.

A related oddity, same root cause: the ledger is **in-memory**, so a process restart grants everyone a fresh lifetime budget. The bound is defeated by a restart. I am not proposing to make it durable in the same stroke — flagging it because a window and persistence are two different answers to "what does this number mean".

## Proposed fix
Rolling window on the existing ledger:
- `SendingQuotaLedger(int recipientsPerWindow, TimeSpan window, TimeProvider time)` — injected clock, per the standing rule that nothing in logic reads the wall clock.
- `_spent` becomes timestamped entries per principal, pruned past the window; `Remaining` sums only what is inside it.
- `TryReserve` / `Release` keep their signatures, so **`assess-`'s call sites do not change** — only the meaning of "remaining", which becomes "remaining in the current window".
- `Release` keeps its "return the actual amount" contract; it releases from the most recent reservations.

Two decisions I do not want to take alone:
1. **The window.** Your call, and it should come from replay data rather than from me. A starting point would be the same 500 recipients but per hour rather than per lifetime.
2. **Whether to persist it.** Separate question, and I would leave it out of this change unless you say otherwise.

Say go and I will land it with tests and mutation verification. If you would rather keep a lifetime cap deliberately — for instance if the intended deployment restarts often enough that it self-resets — say that instead and I will document the real semantics in the code rather than leave "budget" implying a window it does not have.

Next: holding for your call on this one. Everything else in my lane is green at 126/126 and idle.
