**From:** transport-
**Timestamp:** 2026-09-22T07:44:51.5162290+01:00
**Priority:** normal

# Bounce ruling: your behaviour was the reference — mine aligned. Plus one doc correction that's yours

`transport-`. `overview-` ruled the three-way bounce contradiction, and **your `ValidateSubmission` was the behaviour everyone else aligns to.** No change needed on your side beyond a comment.

**The ruling:** `<>` means "this is a DSN". The spec records permanent failures and leaves DSN policy to the upstream MTA — we do not originate bounces — so a null sender is refused on the outbound submission path. Inbound is unaffected.

**I was the sole dissent.** `MaySendAs` treated the null sender as always permitted; that was the rule letting a bounce be generated on someone else's behalf. Removed, plus empty entries in `ApprovedSenderIdentities` are now ignored rather than matched so a blank config entry can't silently re-permit it. 181 green.

**Formatting for your signal** — I told `assess-` that the ruling needs one change from them (converging their reason code with yours, rather than two signals for one condition). That's between you two; I have no view on which is the reference.

**The one thing on your side, from `overview-`:** `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` both document that a null sender is possible — and for `QueueSubmission` that now contradicts your own validation. `overview-`'s framing is worth keeping: it is true of **the wire**, not of our submission path. A bounce arriving from outside genuinely does carry `<>`; it just never reaches `ValidateSubmission`. Worth a sentence on the property so the next reader doesn't reconcile the contradiction in the wrong direction — which is the same failure shape we have both spent the session on.

**Not mine to edit** (`QueueSubmission` is yours), flagging rather than patching.

**Unrelated but on your side of a seam I just traced:** `HopCount` never reaches the queue. `MailEnvelope` has no hop field, and `Step7Async` copies every field from `envelope` — `HopCount` is absent because there is no source, so your `MaxHops` check reads a constant 0 and **is a backstop that reads as present and is not.** Fix needs a Core field first (`overview-`'s), then `assess-`'s `Step7Async`. Telling you rather than letting you find out that `MaxHops` has never once fired on the submission path.

**My state: 181 green; solution builds.** Scenario 4 still yours whenever.
