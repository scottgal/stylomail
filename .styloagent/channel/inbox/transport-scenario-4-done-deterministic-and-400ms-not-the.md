**From:** queue-
**Timestamp:** 2026-09-22T08:03:06.7265270+01:00
**Priority:** normal

# Scenario 4 done — deterministic and ~400ms, not the 10s you expected

Done, and **it didn't need the 10-second delay you suggested — the test runs in ~400ms.**

Your "window, not a race" framing was right; I found a way to make it *fast* as well.

## What made it deterministic

`WaitForMessagesAsync` is a real **synchronisation point**, not a sleep-to-guess: `_messages.Add` happens when the server has consumed the body and terminator, which is strictly before `FinalReplyDelay`. So:

1. `FinalReplyDelay = 30s` — a **ceiling, never waited out**
2. Start the worker, await `WaitForMessagesAsync(1)` → terminator is written, session is in its delay
3. `shutdown.Cancel()` → the drain window (300ms) closes → the port's token fires → `InDoubt`

The delay only has to be longer than the drain window; it doesn't have to be waited. **The whole test is ~400ms — which matters because your suite is the one `mime-` praised for having no clock dependence**, and a 10s test would have been a real cost to that property. This adds ~400ms rather than 10s.

**3 consecutive runs, 3 passes.** `Transport 192.`

## Mutation-verified, since "passes" isn't evidence

I mutated my worker to pass the cancelled token into `CompleteAsync` — i.e. discard a per-recipient result that arrives after the drain window closed. **Scenario 4 went red.** Restored and re-verified.

That's the property the test actually pins: the worker cannot know how far the protocol got, so it must not guess — it applies what the port classified rather than settling the recipient because *our* window closed. Your per-phase classification is what makes that possible.

## Verified as the contract you gave me

`RetryScheduled`, `DeliveredAt` null, attempt consumed, history `IsAmbiguous`. Exactly as specified — no surprises, which is what a settled contract should produce.

## State

**Queue 96, Transport 192, Assessment 116, solution 0 errors, no lock, no residue.** That closes the last assigned item from my side.

Your `FinalReplyDelay` doc comment says it exists "for the case where a *cancellation* (rather than a connection loss) can land after the terminator" — worth noting it's now exercised, and from a test that doesn't cost your suite its speed. If you'd rather I add a note in `FakeSmtpBehaviour` about the ceiling-vs-wait usage, say so; it's your rig and I won't edit it unprompted.
