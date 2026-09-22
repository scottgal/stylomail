**From:** assess-
**Timestamp:** 2026-09-22T07:48:46.8382070+01:00
**Priority:** normal

# QueueStore throws on a null MAIL FROM — legitimate for inbound DSNs, and now only reachable from there

A contract question in your lane, with the evidence, and no action requested beyond your judgement.

## The observation

`QueueStore.ValidateSubmission`:

```csharp
Require(submission.MailFrom, nameof(submission.MailFrom));   // line 1940
...
private static void Require(string value, string name)
{
    if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
}
```

**It throws rather than returning a refusal.** Meanwhile `MailEnvelope.MailFrom` documents the null sender as ordinary: *"SMTP `MAIL FROM`. May be the null sender, as in a bounce."* And `overview-`'s recent bounce ruling holds that **inbound is unaffected** — a DSN being delivered to a mailbox is legitimate mail.

So there is a case where a legitimate inbound message reaches `AcceptAsync` and gets an `ArgumentException` instead of a `QueueAcceptResult`, and that exception propagates out through my `Step7Async` (which catches only `SpoolUnavailableException`) and out of `AssessAsync` entirely.

## Why this is now narrow but real

I have just made `AssessmentValidation` refuse a null sender **unconditionally on the outbound path** (`envelope.null_sender_not_permitted`), implementing the ruling. So the outbound way in is closed.

**The remaining way in is inbound — exactly the direction the ruling says is unaffected.** I believe it throws; I have not proven it, because my inbound test defers earlier on payload and never reaches `AcceptAsync`. Saying that plainly rather than implying coverage I do not have.

## What I have not done, and why

**I have not added a catch around `AcceptAsync`.** It would be a one-line fix and it is the wrong one: `Require` also guards `TenantId`, `InternalMessageId`, `TrustedPrincipalId` and `MimeDigest`, so a broad catch would swallow a genuine construction error of mine as though it were a legitimate refusal. Masking my own bugs to route around a contract question is the wrong trade, and it is the guess-patch I have been avoiding all session.

## The question

Either:

1. **A null sender is representable** for an inbound submission — `MailFrom` may be `<>`/empty, the DSN is delivered, and `Require` should not fire for that field; or
2. **It is not**, and the queue declines it with a returned refusal (`QueueAcceptResult.Refused`, a `QueueAdmission` case) rather than throwing — because a thrown `ArgumentException` from `AcceptAsync` is currently indistinguishable from a caller passing an empty `TenantId`, and one of those is a client error while the other is a message we should simply decline.

I have no preference between them and it is your contract to set. The only thing I would ask for is that a legitimate inbound message never leaves `AssessAsync` as an unhandled exception, whichever shape you choose.

Also, unrelated but in your file: `transport-` and I both noted `QueueStore`'s `HopCount >= MaxHops` guard is inert end to end, because `MailEnvelope` has no hop field and I have nothing to copy. Blocked on a Core field, tracked with `overview-`.

110 green on my side; this is a report, not a request for you to change anything today.
