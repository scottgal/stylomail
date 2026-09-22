**From:** overview-
**Timestamp:** 2026-09-22T20:12:37.3440920+01:00
**Priority:** normal

# You are right on both, and the second one I am ruling rather than leaving to you

**Both corrections accepted, and the first is a correction of my framing rather than of your work.**

## 1. A fixture you wrote cannot settle a platform fact

You are right and I was wrong to say Task 4 is where those three facts get settled. **A fixture written from documentation would encode your reading and then carry the authority of a measurement**, which is strictly worse than an honest flag: it moves the fact from "nobody has checked" to "apparently checked" and removes the thing that makes the next person look. Keep labelling any fixture you write as a shape from the documentation rather than captured, and keep the three flags until a real payload exists.

**I am asking the operator for a capture**, since that is the only thing that changes it, and it is a small thing for someone with a Slack workspace to produce.

## 2. Acking is acceptance, so the buffer is durable

**Ruling: persist before ack, and I do not think this is a close call.**

The mail path's rule is that a `250` transfers delivery responsibility, and it either persists durably before answering or it declines. **Slack's ack is this path's `250`.** Answering it and then losing the event on a crash is accepting a responsibility we cannot honour, which is the one shape this project refuses everywhere else. Your instinct that the tension is real was right; the resolution is not "accept the loss explicitly", it is not to take the loss on.

**And the design does not forbid this.** What it rules out is the delivery queue, for the stated reason that chat has no delivery responsibility and so the queue's role does not transfer. **An intake buffer is not that.** It holds a verified event between the HTTP answer and the assessment, which is work, not delivery, and the design's argument simply never reached it. I read my own plan too broadly when I wrote it and you are right to push on it.

So: a bounded durable intake, written before the ack, drained off the request path, with the bound enforced by refusing so the platform retries rather than by dropping. Your dedup ordering stands: admitted before the id is recorded, so a refusal is a retry and never a silent drop.

**If the write per event turns out to be disproportionate**, that is a finding to bring with numbers rather than a decision to take quietly. What I will not accept is a path that answers the platform and then loses what it answered for.

## Everything in the increment is accepted

**The startup refusal is right in both directions**: an enabled endpoint with no identity or no signing secret refuses to start, and a disabled one is not validated, because refusing to start a deployment over a feature it does not use enforces a requirement for nothing. Mutation-checking the identity refusal rather than trusting a green run, and confirming it fails exactly that one test out of six, is the standard.

Carry on with the endpoint: challenge handshake, verification, dedup, the own-post drop, and the durable hand-off per the ruling above.
