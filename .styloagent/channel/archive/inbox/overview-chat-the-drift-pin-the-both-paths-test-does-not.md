**From:** chat-
**Timestamp:** 2026-09-22T20:01:05.7688870+01:00
**Priority:** normal

# chat- the drift pin: the both-paths test does not exist, here is why and what I pinned instead

The drift pin you asked for is in, but **not in the form you specified, because that form does not exist.** I tried it four ways before concluding that, and the conclusion is the useful part.

## Why equivalent evidence down both paths cannot be built

I got as far as both paths running and comparing, and they disagree: the mail path decides `Defer`, chat proposes `Hold`. Chasing that to ground, the evidence sets differ **structurally at three points, none of which is the composition the test is supposed to be about**:

1. **Chat always records twelve semantic dimensions `Unavailable` and three deterministic signals.** The mail path's fake contributes one deterministic signal, and its semantic unavailable set arrives by a different route with different neighbours.
2. **The mail path carries relationship profiles and an acceptance step** that chat has no counterpart for by design.
3. **The mail path's semantic-outage override turns an outage into a declined responsibility.** I switched `DeclineResponsibilityOnSemanticOutage` off to isolate it, and it still returned `Defer`, because step 7's refused acceptance produces `Defer` too. Each of those is a real designed difference.

A comparison across all that measures the differences rather than the drift, so it would have to be built by making the two paths produce identical evidence, which means changing what the producers emit. That is not a test of the composition any more.

**One thing that fell out of the attempt and is worth having:** my first version compared a chat member against a mail envelope built by `Builders.Envelope()`, which **defaults to `Inbound`**. So the two paths were being asked about different directions entirely. That is the same confound as the call you refused, reappearing inside my own test, and it is now written into the test that survives.

## What I pinned instead

**`The_chat_path_decides_with_the_standard_policy_composition`.** It takes chat's own assessment, recomputes the risk index and the policy decision from that assessment's evidence using `Builders.Options()`, the standard `CompositeRiskScorer` and a standard `MailPolicyEngine` with `Direction.Outbound`, and asserts both the risk index and the action match what the assessor produced.

**That catches the drift that actually matters.** Chat composes these itself rather than sharing extracted steps, so it fails if chat ever drifts to different weights, a different engine, a different `PolicyOptions`, or a direction other than the one the membership derives. It does not catch a change made *inside* the shared engines, because they are shared.

I am not claiming it is the pin you asked for. It is the closest one that can be built honestly, and your fallback was the remark, so this is strictly better than the fallback rather than instead of it.

## Measured

Build 0 warnings, 0 errors. Whole solution **1404 passed, 0 failed, 23 skipped**. Assessment 134.

## Still open

Questions one and two from my earlier message are unanswered and still block the same two things: the inbound sender qualification for external authors, and whether the channel takes the recipient-and-relationship slot. The observed-state write is blocked behind the second, since where the profile is written determines what fan-out evidence could ever be. Everything else in Task 3 is done and frozen.
