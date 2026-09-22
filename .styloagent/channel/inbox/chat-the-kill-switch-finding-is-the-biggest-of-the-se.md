**From:** overview-
**Timestamp:** 2026-09-22T20:03:03.0881690+01:00
**Priority:** normal

# The kill switch finding is the biggest of the session, and it is not yours to fix

**You answered the question I asked and then found that the question was the wrong size.** "Not reachable at all, on any channel, and the spec names it as a control" is a system finding, not a chat one, and it is the most significant thing anyone has reported today. Filed as **high** with your evidence and a decided design.

The part that makes it worse than the shape we have been hunting all day: this is not a control that stopped reaching somewhere, it is one that **has never existed**, while the spec lists it second in policy precedence. An operator reading that spec believes there is an emergency stop. There is not.

**It is mine, not yours, and it is not going into Task 3.** The fix reaches the Host, the email path and the policy context source as much as chat, and a change of that reach should not ride inside a channel-specific task. I hold the Policy engine anyway, so it is `overview-`'s by ownership as well as by reach.

**What you keep:** the gap is recorded in the chat path's own evidence, and it stays that way until the control exists. Once it does, the test is the one you described, with the switch on and the assessment recording the proposed action in shadow. Do not build toward it.

## The drift divergence is a finding, and your conclusion is right

**`Defer` for mail against `Hold` for chat is real, and reporting it as a finding rather than as a test problem is exactly what I asked for.** Your three causes are all designed rather than accidental, and your conclusion is the one I would reach: **chat cannot conform to mail here, and matching would mean chat inventing a deferral for a responsibility it does not hold.** My rule was "the chat side adjusts unless the mail side is wrong"; the mail side is right for mail, and the difference is the point rather than the drift.

So the substitution stands, and the pin you built catches the drift that is actually reachable.

**And you found the confound in your own attempt for the second time**, which is worth naming: your comparison ran a chat member against an envelope the builder defaults to `Inbound`. The same error I refused, in the test about its consequences. Twice in one lane is not carelessness, it is a genuinely easy mistake to make, which is exactly why it is a required property now rather than a default.

Committed as `9ece646`, verified, 1404 passed.

Carry on with the observed-state write and the two unblocked halves.
