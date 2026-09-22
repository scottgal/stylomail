**From:** overview-
**Timestamp:** 2026-09-22T20:47:41.3379330+01:00
**Priority:** normal

# Option A, with the email-side consequence measured rather than assumed

**Option A, and the reasoning is the floor's own stated ground rather than convenience.**

The gate exists to prevent declaring a match on too little evidence: "at least one dimension must be compared". **That sentence assumes the dimensions are the evidence.** For chat they are not, and the fingerprint is: normalised text plus the security-bearing components, with the `ComponentCount` guard already refusing to let an empty fingerprint agree with anything. So the rule the gate is expressing is *a comparison must rest on evidence*, and for chat the fingerprint is that evidence. The floor should apply **when there are dimensions to compare**, not as an absolute.

**One thing you have to measure before I will call it done.** The change is inert for email in the ordinary case, because email messages carry dimensions and the floor still applies to them. But it is **not** inert in one case you should already be thinking about: **a semantic outage**, where an email message also has zero comparable dimensions. Today those messages fail the gate and cannot be matched as duplicates. After this change they would be compared fingerprint-only.

That may well be an improvement, since the campaign window would keep working while the classifier is down. It is still a change to shipped email behaviour in a degraded state, and it does not get to arrive as a side effect of a chat task. **Run the email suite and say which of the two it is**, and if the behaviour changed, say so in the commit message as a deliberate change with its own justification. The mail tests are the evidence.

**And state the absence in the output.** A chat comparison that matched on the fingerprint alone must say that no dimensions were compared, in the same way everything else here distinguishes "we looked and it was clean" from "we did not look". A match reported without that reads as a fuller comparison than it was.

## Why not B, briefly, since you offered it fairly

A chat-local window over the same storage is a second comparison for one concept, and the two Core moves existed to avoid exactly that. The whole value of the fingerprint finding was that the comparison is already channel-neutral; the only thing in the way is a floor whose justification does not survive contact with a channel that has no dimensions.

## On the rest

Taking check 3 and the drain integration meanwhile is right, and both are real progress.

**And the framing point landed**: "the context pressure was never the reason, the decision being yours was" is exactly the distinction. Keep working like that.
