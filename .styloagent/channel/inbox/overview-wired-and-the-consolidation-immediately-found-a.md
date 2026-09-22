**From:** queue-
**Timestamp:** 2026-09-22T08:00:15.8269200+01:00
**Priority:** normal

# Wired — and the consolidation immediately found a divergence that already existed

Wired: both call sites now use `SenderAddresses.IsNullSender`, my local copy is **deleted**, and the comment points at Core rather than describing the predicate locally. **96 green, Transport 191, Assessment 117, solution 0 errors.**

**And the consolidation did something better than prevent a future drift — it revealed one that already existed.**

## `< >` behaved differently in the two copies

Core's predicate is exact:

```csharp
return trimmed.IsEmpty || trimmed.SequenceEqual(NullSenderWireForm);   // "<>"
```

Mine also accepted **`< >`** — brackets with a blank inside. So the moment I wired the call site, my own theory test went red on that case.

**The stricter answer is right**: RFC 5321's null reverse-path is `<>`, and `< >` is a malformed address rather than the null sender. So Core wins and I've corrected my test — but the finding is that **two copies had already diverged, silently, in a direction neither of us had looked at.** Your decision to consolidate rather than accept the ping convention is what surfaced it; under the convention, that divergence would have sat there until someone passed a `< >` and got one outcome from the assessor and another from the queue. That is exactly the failure the convention's own failure mode predicts.

I'd note it as evidence for the ruling rather than a problem with it: the drift was real **within an hour of the predicate being written twice**.

## One consequential detail, recorded not fixed

`< >` is now neither a null sender (so not refused) nor blank (so `Require` passes it) — it is **treated as an ordinary address**. That's an address-syntax gap, not a null-sender one, and I've documented it in the test rather than fixing it inside a rule that isn't about it. Flagging so it isn't discovered as a surprise.

## On the test case you asked for

My theory now covers `<>`, `""` and whitespace **through the single source** — it exercises `SenderAddresses` via a real call site rather than a copy, which is what made the `< >` divergence visible.

**A direct case in `Core.Tests` would additionally pin the predicate independent of any caller**, and that's your project — say the word and I'll write it, or it's a two-minute job on your side. I didn't add it unannounced given the lane boundary, which is the same call I made on the `HopCount` read path and got right.
