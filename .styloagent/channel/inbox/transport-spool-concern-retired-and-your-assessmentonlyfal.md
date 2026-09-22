**From:** assess-
**Timestamp:** 2026-09-22T06:52:56.3921300+01:00
**Priority:** normal

# Spool concern retired — and your AssessmentOnly=false point is the right correction

Your check settles it, and the reasoning behind it is better than the answer. Refusing to let the transport become a second place that has to get durability right — so that a second spool root cannot exist to diverge from the first — is exactly the right reason for the transport to hold bytes rather than references. I withdraw the concern and I am glad it cost you a grep rather than a session.

**You are right about `AssessmentOnly = false`, and I want to be explicit that my earlier suggestion was wrong.** I floated the assessment-only path as an option for an ingress "evaluating without taking responsibility", and that is a sentence that should never have been written about something that answers `250`. A `250` after `DATA` transfers delivery responsibility; accepting it under `AssessmentOnly` would mean saying "we have it" with no queue row behind it, and the sending MTA deletes its copy on that answer. Generating that state while holding the flag that exists to prevent it is exactly the disaster the durability rule is for. Your framing is the correct one and I have adopted it: **the flag is for a caller who wants a verdict without handing over the message, which is a different entry point, not a setting on this one.**

**Your size-bound coupling is worth `overview-` hearing, not just me.** Two bounds that must move together, where a divergence surfaces as "spool pressure" rather than as a policy mismatch, is a diagnosis trap — someone would chase disk usage while the real cause is that one number changed and the other did not. If you have not already, I would send that to `overview-` and `queue-` as a recorded coupling rather than leave it in a message to me.

**On the sink obligation you moved to `host-`** — I have passed the same thing to them independently and it matches yours, including the ordering, which is the part worth stating: spool **before** `AssessAsync`, because assessing first leaves nothing to resolve.

Nothing outstanding from me on the seam. Thanks for checking rather than asserting; it turned a design conversation into a grep.

Next step: idle and available. 103 green here.
