**From:** chat-
**Timestamp:** 2026-09-22T20:01:53.8514860+01:00
**Priority:** urgent

# chat- the kill switch is not merely mail-typed, it is not wired at all; and the drift test is green with the divergence reported as a finding

Two things, and the first is bigger than the question I was asked to answer.

## The kill switch is unreachable by every path, email included

You asked whether it is reachable without the mail-typed `GetAsync`. It is not reachable **at all**, and that is a system finding rather than a chat one.

- `EmergencyKillSwitchEngaged` exists on `PolicyContextInput` and is read by `MailPolicyEngine`.
- The **only** shape it can arrive through is `IAssessmentPolicyContextSource.GetAsync(MailAnalysisInput, ...)`.
- **The only implementation is `StaticPolicyContextSource`**, whose own summary is "Supplies nothing. The default until an operator wires real state in", and which returns a default `PolicyContextInput` every time.
- `MailAssessor` defaults to it, and **nothing in the Host or anywhere else constructs a real one.** I grepped the whole of `src` for anything that could set the flag true and found only the engine reading it and the assessor passing it along. The only places it is ever set to true are two `MailPolicyEngine` unit tests.

**So an operator cannot engage the emergency stop today, on any channel, and every email assessment in production has been made with it silently false.** That is the "claim the system cannot keep" shape you have been rooting out all day, except that here the control has never existed rather than having stopped reaching somewhere. I have not touched it: making it configurable means deciding where the operator state comes from, which is a design decision and yours.

**For chat specifically**, my code records the gap and will keep doing so. With your rule applied, once there is somewhere for it to come from, chat reads it; a system-wide safety control read by every path that can act is what I would build, and a good chat test is the one you described, with the switch on and the assessment recording the proposed action in shadow.

## The drift test is green now, and the divergence is a finding

**Your suite run caught it mid-increment.** It now passes, and I want to be exact about what happened, because "the test needed adjusting" is the outcome you warned against and I am not claiming otherwise.

**The two paths do produce different actions for equivalent evidence: `Defer` for mail, `Hold` for chat. That is real, and I am reporting it as a finding, not as a test problem.** I chased it to ground and there are three causes, all of them designed rather than accidental:

1. **Mail's semantic-outage override.** With its classifier down, it declines responsibility and defers, because it is in the delivery path and can. I switched `DeclineResponsibilityOnSemanticOutage` off to isolate it and it still returned `Defer`, which leads to the second.
2. **Mail's step 7**, where a refused acceptance also produces `Defer`. Chat has no acceptance and no delivery responsibility, so it has nothing to decline.
3. **The evidence sets differ structurally anyway**: chat always records twelve semantic dimensions unavailable and three deterministic signals, mail contributes a different shape and carries relationship profiles.

**Chat cannot conform to mail on this**, and I think that is the correct direction rather than a gap: matching would mean chat inventing a deferral for a responsibility it does not hold. By your rule the chat side adjusts to match unless the mail side is wrong, and here the mail side is right for mail and the difference is the point.

**What I pinned instead** is that chat composes the standard stack: it recomputes the risk index and the action from chat's own evidence with `Builders.Options()`, `CompositeRiskScorer` and `MailPolicyEngine` at `Direction.Outbound`, and asserts both match what the assessor produced. It fails if chat drifts to different weights, a different engine or a different direction. It cannot catch a change inside the shared engines, because they are shared.

**And the attempt found my own version of the error you refused:** my first comparison ran a chat member against `Builders.Envelope()`, which defaults to `Inbound`, so the two paths were being asked about different directions. Same confound, in my own test.

## Received and in progress

Both scope rulings, and the observed-state steer: **unconditional on the outcome, recorded after the assessment, in the pool the derived direction selected, because observed state is what velocity and drift are computed from.** That is what I am implementing now, along with the distinct scope kind and the conversation-type component in the key. I will verify the conversation type against a recorded payload rather than taking it from you.

Nothing is committed from this stretch; the tree is green and I will report the scope work when it lands.
