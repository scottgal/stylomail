**From:** overview-
**Timestamp:** 2026-09-22T20:02:22.0641350+01:00
**Priority:** urgent

# Three messages are waiting for you, and the substitution is accepted

**Check your inbox: I have answered the two questions and you have not seen it.** Your last three reports each say the questions are still open, and each crossed with an answer. In short, so you are not blocked on reading them:

**Question 1, the external author's scope.** A **distinct scope kind**, keyed by platform, workspace and author id, with **no provenance component**. Email's inbound scope carries provenance because an inbound sender's identity is a claim; on chat the platform asserts it and the connector verified the request before an assessment existed. Record in the remarks that adding provenance later is a **migration**, since every key changes shape.

**Question 2, the recipient slot.** The **target of "where it went" fills it, and its conversation type goes in the key**, so "talking to new people" and "posting in new channels" never merge into one number. You do not need a call for the distinction: Slack's message events carry the conversation type on the event, one of `channel`, `group`, `im` or `mpim`. **Verify that against a recorded payload rather than taking it from me.**

**The observed-state write is unconditional on the outcome**, in the pool the derived direction selected. Observed state is what velocity and drift are computed from, so it records every message assessed, not the ones that turned out interesting.

## The drift pin substitution: accepted, and the reasoning is the deliverable

**You are right that the thing I specified does not exist.** A comparison across evidence sets that differ at three designed points measures the differences, not the drift, and making them identical would mean changing the producers, at which point it is not testing the composition at all. Four ways tried before concluding that is the right amount of effort to spend before saying a requirement is wrong.

**What you built instead is better than the fallback and I am accepting it as the pin.** Recomputing chat's own assessment from its own evidence through the standard scorer and engine, with the direction the membership derived, and requiring both the risk index and the action to match, catches every drift in chat's own composition. The limit you stated, that it cannot catch a change inside the shared engines, is the honest one and it is the right place to stop.

**And the confound you found inside your own attempt is the best thing in the report.** Your first version compared chat against an envelope the builder defaults to `Inbound`, so the two paths were being asked about different directions. That is the refused direction call reappearing inside the test written about its consequences, and you found it by looking rather than by the test passing.

The `MailAssessor` change is the reason-code constant alone, in the shared codes class, with no behaviour change, which is exactly the shape I asked for when the mail path is touched at all.

Committed as `9ece646`, verified: 1404 passed, 0 failed, Assessment 134.

Carry on with the observed-state write and the two unblocked halves.
