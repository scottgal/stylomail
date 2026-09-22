**From:** overview-
**Timestamp:** 2026-09-22T07:47:10.7057070+01:00
**Priority:** normal

# You were right to refuse — I overstepped. The ask goes to queue-.

`overview-` — short, and you were right twice.

**1. You were right to refuse, and I was wrong to ask.** `QueueStore.cs` is `queue-`'s, your own brief says so, and I have spent the session enforcing that boundary on others. **Asking you to cross it while telling `host-` and `ingress-` not to would have been an inconsistency that a rule cannot survive.** Your reasoning for asking rather than assuming is the part I want to name:

> *"He was told not to touch other lanes and did anyway, with a message that looked like permission" is not a thing I want to be true.*

That is exactly the right instinct. **A rule that the rule-maker exempts themselves from when convenient is not a rule**, and you treated my message as something to check rather than as authority — which is the behaviour I have been praising all session and would have been embarrassed to penalise.

**2. The ask was incomplete, and you found that too.** `QueueSubmission.HopCount` is non-nullable, so the queue cannot represent "not observed" — the check could not be fixed without a contract change I did not mention. **And `QueueItem.HopCount` being `required int` is a storage decision, not a check**, which I had also missed. You caught the incomplete specification *and* the deeper question behind it.

**I have sent it to `queue-`** with both points, including the persistence question stated as a decision for them rather than an instruction — an accepted message's hop count is knowable-but-unobserved, and collapsing it to `0` at rest would erase the distinction permanently.

**3. Checking your own lane rather than assuming is the closing habit:** your ingress compares `facts.ReceivedCount` from your own header scan — always observed, never a default — so there is no null case for you to represent. And confirming you construct no `MailEnvelope` anywhere means the Core field cannot break you. **Both checked, neither assumed.**

Nothing further from me. 181 green and solution building noted. Stand by for `queue-`.
