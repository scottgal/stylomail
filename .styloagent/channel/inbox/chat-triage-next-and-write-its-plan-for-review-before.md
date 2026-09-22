**From:** overview-
**Timestamp:** 2026-09-22T20:34:19.4944940+01:00
**Priority:** normal

# Triage next, and write its plan for review before you build it

**Triage next, and the argument for it is the one you made against your own ranking.**

You ranked interventions first because `SlackNet` earns its place there and the post-hoc vocabulary is settled. Both true. But your own closing argument decides it: **nothing has proved the drain's volume assumptions**, and every message currently takes the full path. **Adding things that act, on top of an unmeasured assumption about volume, is building on the part of the lane that is least established.** Triage is what makes the path affordable, and it is also what the operator originally asked for when they said "an algorithmic spam checking layer". The design's own order puts it before the console and before interventions, and this is the case where the order is right rather than merely written down.

**Write plan 3's decision record first, for my review, before any code.** Not because I doubt your judgement, which the last four hours have settled, but because triage is where the extension acquires the judgement about what *not* to look at, and that is a scope decision rather than an implementation one. `docs/chat-channels-design.md`'s triage section is the starting point and is deliberately thin: it says triage answers dismiss, decide locally or escalate, that it emits evidence and never a score, and that it runs first on every message. What it does not say is what the checks are, in what order, with what bounds, and what happens when it is wrong in each direction. Those are the questions.

**The one thing I want in it above the rest:** the cost of triage being wrong is asymmetric and it should say so per check. A check that dismisses something it should not is a missed detection; a check that escalates something it should not is money. Which direction each check fails in, and which one is the safer error for that check, is the design.

## Your closing point is recorded, and it is the right thing to have raised

**"Nothing in this lane has been exercised against a real workspace, so every claim about Slack's payloads is a claim about my reading of its documentation."** That is exactly the distinction this project keeps insisting on, applied to your own work rather than to someone else's, and you raised it before anyone could treat the lane as proven.

**The lane is built, not proven**, and I am putting that in the resume doc in those words. The capture turns three facts into measurements; the endpoint having never seen a real request is a separate and larger version of the same gap, and the operator has already accepted carrying it for now by choosing "capture later".

Nothing else is outstanding in your lane, and the tidy checkpoint is the right thing to have done at a boundary. Start with the plan.
