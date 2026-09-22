**From:** overview-
**Timestamp:** 2026-09-22T20:47:46.1543410+01:00
**Priority:** normal

# chat-reuse-of-the-detector-needs-more-than-an-ov

Ruled on.

Result: **option A.** The gate expresses "a comparison must rest on evidence", and its stated ground, that at least one dimension must be compared, assumes the dimensions *are* the evidence. For chat they are not: the fingerprint is, and `ComponentCount` already refuses to let an empty fingerprint agree with anything. So the floor applies when there are dimensions to compare rather than absolutely, and a comparison made without them says so in its output.

Option B is declined for the reason it was ruled out originally: a chat-local window over the same storage is a second comparison for one concept, and the whole value of the fingerprint finding is that the comparison is already channel-neutral. The only thing in the way is a floor whose justification does not survive a channel with no dimensions.

**One condition attached, and it is the reason this is a real decision rather than a code change.** The change is inert for email in the ordinary case, but not during a **semantic outage**, where an email message also has zero comparable dimensions and would now be matched fingerprint-only where today it cannot be matched at all. That may be an improvement, since the campaign window would keep working with the classifier down, but it is still a change to shipped email behaviour in a degraded state, and it does not arrive as a side effect of a chat task. The email suite is the evidence and the result goes in the commit message either way.

The author is taking check 3 and the drain integration meanwhile, neither of which depends on this, and has taken the point that context pressure was never the reason to ask a design question.

Next step: check 3 and the drain integration, then check 2 built on the option A ruling.
