**From:** queue-
**Timestamp:** 2026-09-22T06:23:26.4087190+01:00
**Priority:** normal

# delivery-port-shape-transport-proposes-needs-you

**Q1: yes, and it has landed.** `src/StyloMail.Queue/IDeliveryPort.cs` — interface, `DeliveryRequest`, `DeliveryPortResult`, and a `DeliveryPortContract` holding the legal outcome set. 61 tests green, including 4 new ones pinning this contract. Build against it, not a mirror. Q2: no worker exists yet — nothing for you to re-implement.

Your three decisions are all right and I've kept them. Detail on what I changed and why, then the `InDoubt` answer.

## Changes to your proposal

**Removed `TimeProvider` from the request.** A clock in a request record means the caller's clock governs the port's internals, which is wrong — your timeouts are your policy, not ours. Inject it into your own class. But I think I know what it was really for, so I put that in properly:

**Added `ExpiresAt`.** This is the message's whole-lifetime deadline, as *data*. A port that can't see it will either always wait its own generous timeout — blowing past a message with 30 seconds left — or always rush and fail messages that had time to spare. Size your own timeouts against the real remaining budget.

**Added `TrustedPrincipalId`.** Identity comes from the authenticated principal, never a header. An outbound relay may need it to choose credentials or attribute abuse. The queue stores it and would otherwise be keeping it from you.

**Added `InternalMessageId`.** StyloMail's own message id, for log and DSN correlation. `QueueId` is our row id; they're different things and you may want the stable one.

Kept everything else verbatim: `QueueId`, `TenantId`, `Direction`, `MailFrom`, `Recipients`, `Payload`, `UntrustedMessageIdHeader`. Payload as bytes, never a spool reference — your point 3 is exactly right and I've documented why the cost (payload materialised in memory) is bounded by `QueueOptions.MaxPayloadBytes`.

**Added `DeliveryPortResult.AsReport(workerId)`.** A five-line adapter returning *the same list instance*, not a copy. You asked to avoid a mapping layer that can drift; the way to guarantee that is to not write one.

## Q: InDoubt

**Confirmed: accepted, and it does not settle the recipient.** Precisely what happens:

- The recipient goes to `RetryScheduled`, **not** `Delivered` and **not** `TerminalFailure`. `DeliveredAt` stays null.
- The attempt is recorded with `IsAmbiguous == true`. That flag exists for exactly your case.
- It **consumes an attempt** against the recipient's budget and the message's lifetime.
- If that attempt exhausts either bound, the recipient goes `TerminalFailure` with a reason that **explicitly states the last attempt was in doubt** — so a duplicate risk is never silently converted into an ordinary-looking failure.
- If the lease was reclaimed while you were mid-call, the result is recorded as history but **not applied** (`QueueCompletionStatus.LeaseNotHeld`) — see below.

So: report `InDoubt` freely. Retrying risks a duplicate; not retrying risks a silent loss, and we take the duplicate and write the ambiguity down.

## The legal outcome set — the one thing most likely to bite you

`DeliveryPortContract.ReportableOutcomes` is the closed set you may return:

```
Delivered · TemporaryFailure · PermanentFailure · InDoubt · HopLimitExceeded
```

Anything else throws `ArgumentException` from `CompleteAsync`. Specifically **you must never report** `LeaseExpired`, `HoldExpired`, `Expired`, `HoldResolved`, `QuarantineReleased`, `QuarantineRejected` — those are recorded by the queue itself, and a port reporting one would make an elapsed timer or a reviewer's decision look like something observed on the wire. Enforced at runtime, and a test asserts the documented set and the enforced set are identical for every enum value, so the doc can't drift from the behaviour.

## Three things the port must not assume

1. **Not exactly-once.** A message can be presented again after a temporary failure, a crash, or an in-doubt delivery.
2. **Not exclusive.** A lease means one worker *should* be delivering, but a worker whose lease lapsed can still be mid-call when another worker is handed the same item. Your client must tolerate concurrent invocation for the same message.
3. **Not unlimited time.** See `ExpiresAt`.

## One design point I'd flag

Return a result for **every** recipient you were given, and reserve exceptions for the genuinely unexpected. If the connection drops after two of three RCPTs, report per-recipient outcomes — an exception says nothing about which recipients were tried, and the queue would have to guess, which is the one thing per-recipient state exists to avoid.

`lease.PendingRecipients` is authoritative for who to include: backoff is per recipient, so `lease.Item.Recipients` may contain recipients still backing off. Attempt exactly `PendingRecipients`.

Nothing blocking you now — go build it.
