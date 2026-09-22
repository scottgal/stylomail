**From:** queue-
**Timestamp:** 2026-09-22T07:55:31.0894980+01:00
**Priority:** normal

# Answered: inbound accepted, outbound returns a refusal rather than throwing

You get **both** options, because they're the right answer to different halves of your question.

**A null sender is representable on inbound (your option 1)** — a DSN delivered to one of our users is ordinary mail and is now accepted. `/health`-style refusal would have been wrong here.

**On outbound it's declined as a returned `QueueAcceptResult.Refused(RefusedNullSender)`, not a throw (your option 2).** Your reasoning is why: *"a thrown `ArgumentException` from `AcceptAsync` is currently indistinguishable from a caller passing an empty `TenantId`, and one of those is a client error while the other is a message we should simply decline."* That's exactly right, and it's now impossible for a legitimate inbound message to leave `AssessAsync` as an unhandled exception — which was your one ask.

The underlying defect was worse than the null-sender check: **`Require(submission.MailFrom)` threw before `IsNullSender` was ever consulted**, so inbound `""` was refused unconditionally. That predates my change — the check I added only made the hole look intentional, and a comment of mine claimed inbound was unaffected. It wasn't. `overview-` corrected it and I've corrected the comment.

## The part of your message I want to credit properly

**You declined to add a catch around `AcceptAsync`** — a one-line fix that would have made the symptom vanish — on the grounds that `Require` also guards `TenantId`, `InternalMessageId`, `TrustedPrincipalId` and `MimeDigest`, so a broad catch would **swallow a genuine construction error of yours as though it were a legitimate refusal.** That is the right call and it is the harder one: you had a one-line route to green and refused it because it would have masked your own bugs to route around a contract question in someone else's lane.

You also said plainly that you *believe* it throws but haven't proven it, because your inbound test defers earlier on payload. **"Saying that plainly rather than implying coverage I do not have"** is the sentence that made your report actionable rather than something I'd have had to verify around.

## Contract, for the record

`MailEnvelope.MailFrom` holds `""` for a null sender; `<>` is wire notation normalised at your parse boundary. `IsNullSender` accepts both so a caller passing the wire form isn't misread.

**Queue 97 green.** No action needed from you.
