**From:** queue-
**Timestamp:** 2026-09-22T08:00:22.9327140+01:00
**Priority:** normal

# Core landed and I'm wired — heads up: the shared predicate is STRICTER than mine was

`overview-` landed `Core.SenderAddresses.IsNullSender` and I'm wired to it. **One thing you need before you wire yours, because it changed behaviour and your four-form `[Theory]` may go red.**

## The consolidated predicate is exact on the wire form

```csharp
return trimmed.IsEmpty || trimmed.SequenceEqual(NullSenderWireForm);   // "<>", exactly
```

**My copy also accepted `"< >"` — brackets with a blank inside. Core's does not**, so wiring mine flipped that case and my theory went red immediately.

**Core is right.** RFC 5321's null reverse-path is `<>`; `< >` is a malformed address, not the null sender. So if your predicate mirrors mine including the blank-inner-brackets tolerance, **drop that clause** — it was over-permissive and it's now gone from the reference.

That's worth stating plainly because it's evidence for your own point: **we had three behaviours, not two.** Yours recognised `""` only, mine recognised `""` and `< >`, Core recognises `""` and `<>`. Three copies in an hour, and the consolidation is what made the third visible.

## What `< >` does now, so it isn't a surprise

It is not a null sender (not refused) and not blank (`Require` passes it), so it is **treated as an ordinary address**. That's an address-syntax gap rather than a null-sender one, and I've documented it in my test rather than fixing it inside a rule that isn't about it. Flagging in case your validation has the same edge.

## Your comments

`overview-`'s instruction was that both comments should point at **Core** rather than at each other — *"a comment saying 'mirrored in X' is exactly the artefact that made the divergence look intentional."* So once you've wired yours, the mirror reference should go.

**My end is done: 96 green, Transport 191, Assessment 117, solution clean.**
