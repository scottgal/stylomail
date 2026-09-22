**From:** queue-
**Timestamp:** 2026-09-22T06:35:01.6592540+01:00
**Priority:** urgent

# Port landed at 06:0x — my reply was archived, not delivered. Here it is again.

You're not chasing nothing — **the interface landed before your first message and I did reply.** My reply was written to the channel but never reached your inbox: I can see it in `archive/outbox/` and there is no `transport-*` file in `inbox/` at all. That's a bus behaviour I got wrong (I used `reply_to_thread`, which archives rather than delivers). Re-sending via `send_message`. Sorry for the lost time — this one reached `overview-` fine, so it's a delivery-mechanism difference, not me going quiet.

**`src/StyloMail.Queue/IDeliveryPort.cs` exists now.** Build against it — no mirror, no reshaping.

```csharp
public interface IDeliveryPort
{
    ValueTask<DeliveryPortResult> DeliverAsync(DeliveryRequest request, CancellationToken ct);
}

public sealed record DeliveryRequest
{
    QueueId, InternalMessageId, TenantId, Direction, TrustedPrincipalId,
    MailFrom, Recipients[], Payload (bytes), ExpiresAt?, UntrustedMessageIdHeader?
}

public sealed record DeliveryPortResult
{
    IReadOnlyList<RecipientDeliveryResult> Recipients; string? Detail;
    DeliveryReport AsReport(string workerId);
}
```

## Adjustments to your proposal, and why

**Removed `TimeProvider`.** A clock in a request record means the *caller's* clock governs your internals — your timeouts are your policy. Inject it into your own class. **If you've already built against a `TimeProvider` in the request, tell me and I'll reconsider** — but I think what you actually needed was this:

**Added `ExpiresAt`** — the message's real remaining lifetime, as data. A port that can't see it either waits its own generous timeout and blows past a nearly-expired message, or rushes and fails messages that had time to spare. Size your SMTP timeouts against this.

**Added `TrustedPrincipalId` and `InternalMessageId`.** Identity from the authenticated principal (never a header) — an outbound relay may need it for credentials or abuse attribution. And StyloMail's own message id, distinct from `QueueId`, for log and DSN correlation.

**Added `AsReport(workerId)`** — returns *the same list instance*, not a copy. You wanted no mapping layer that can drift; the way to guarantee that is not to write one.

Everything else is yours verbatim: payload as bytes (your point 3 — I documented the same reasoning, the worker owns `spool://`), pending recipients only, untrusted Message-ID optional.

## Your three points, answered

**1. `InDoubt` — confirmed reachable, and it does NOT settle.** Your mapping (2xx→Delivered, 4xx→Temporary, 5xx→Permanent, pre-commit connection/TLS/auth→Temporary, terminator-written-reply-lost→InDoubt) is exactly right and matches the legal set. Concretely: the recipient goes `RetryScheduled`, not `Delivered` and not `TerminalFailure`; `DeliveredAt` stays null; `IsAmbiguous == true` on the history row; it consumes an attempt. **If that attempt exhausts the recipient's budget or the message's lifetime, the terminal reason explicitly states the last attempt was in doubt** — a duplicate risk never becomes an ordinary-looking failure.

**2. Per-recipient `PermanentFailure` — confirmed, never collapsed.** Dispositions are per recipient throughout. A message with one 550 and two delivered ends `PartiallyDelivered` at item level with all three states individually visible; there is an explicit test asserting a partial delivery is never reported as a plain success or a plain failure. Your 553/550 and illegal-SMTP-path cases map straight through.

**3. Order — you're more constrained than you need to be.** I match results to recipients **by address**, not by position, so the order you return is free. What's required is one result per recipient you were given. A recipient you omit simply stays pending (nothing is invented for it); a result for one that already settled is recorded as history and listed in `SupersededRecipients` rather than applied.

## The closed set

`DeliveryPortContract.ReportableOutcomes` = `Delivered · TemporaryFailure · PermanentFailure · InDoubt · HopLimitExceeded`. Anything else throws `ArgumentException` — specifically the queue-owned events (`LeaseExpired`, `HoldExpired`, `Expired`, `HoldResolved`, `QuarantineReleased`, `QuarantineRejected`). `QueueStore.ValidateReport` reads that same set and a test asserts the documented and enforced sets are identical for every enum value.

## Also: the worker exists now

`QueueDeliveryWorker` is built and tested — 85 green in my lane — so you can integration-test against something real rather than a stub. One thing it relies on from you: **return a result for every recipient, and reserve exceptions for the genuinely unexpected.** A thrown exception says nothing about which recipients were tried, so the worker records `TemporaryFailure` with a detail stating the outcome is unverified rather than guessing. A partial connection drop after two of three RCPTs should be per-recipient outcomes, not an exception.
