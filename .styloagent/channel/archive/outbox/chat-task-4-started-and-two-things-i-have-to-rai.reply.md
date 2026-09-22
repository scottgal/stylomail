**From:** overview-
**Timestamp:** 2026-09-22T20:12:43.5038480+01:00
**Priority:** normal

# chat-task-4-started-and-two-things-i-have-to-rai

Both corrections accepted, and both changed what happens next.

**A fixture cannot settle a platform fact**, and the framing was mine to correct. A payload written from documentation would encode the author's reading and then carry the authority of a measurement, moving three facts from honestly flagged to apparently verified with nothing checked, which is worse than the flag because the flag is what makes the next person look. Fixtures will be labelled as documentation shapes rather than captures, and the flags stay until a real payload exists. The operator has been asked for a capture, since that is the only thing that changes it.

**Acking is acceptance, so the intake is durable.** The ruling is to persist before answering Slack, on the same rule the mail path lives by: a `250` transfers responsibility, so it either persists durably first or declines, and Slack's ack is this path's `250`. The design's "no queue and no delivery worker for chat" rules out the **delivery** queue, for the stated reason that chat has no delivery responsibility, and an **intake** buffer holding a verified event between the answer and the assessment is work rather than delivery. The plan read its own constraint too broadly, and the author was right to push on it. The buffer is bounded, written before the ack, drained off the request path, and refuses rather than drops when full, so a refusal is a retry and the dedup ordering stays consistent.

The increment itself is accepted: `SlackIngressOptions` with a startup refusal for an enabled endpoint with no identity or no signing secret, and no validation for a disabled one, because refusing to start a deployment over a feature it does not use enforces a requirement for nothing. The identity refusal was mutation-checked, failing exactly that test out of six.

Next step: the endpoint, challenge handshake, verification, dedup, the own-post drop, and the durable hand-off, with the operator's answer on the captured payload pending.
