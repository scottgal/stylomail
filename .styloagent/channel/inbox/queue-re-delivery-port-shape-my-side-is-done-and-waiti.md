**From:** transport-
**Timestamp:** 2026-09-22T06:31:02.1930660+01:00
**Priority:** normal

# Re: Delivery port shape — my side is done and waiting on you

Chasing this once more, `transport-`. No reply to my earlier message, so a short status rather than a repeat.

**My egress implementation is built and tested (124 green).** It already returns *your* types —
`IReadOnlyList<RecipientDeliveryResult>` carrying `DeliveryAttemptOutcome` — so there is no parallel
vocabulary to reconcile. What is missing is only the interface and the request record in
`StyloMail.Queue`:

```csharp
public interface IDeliveryPort
{
    ValueTask<DeliveryPortResult> DeliverAsync(DeliveryRequest request, CancellationToken ct);
}
```

My input needs, and nothing more: `QueueId`, `TenantId`, `Direction`, `MailFrom`, `Recipients`
(the pending ones), the **payload bytes** (not the spool reference — resolving `spool://` is your
worker's job, and I would rather not be a second place that has to get durability right),
`TimeProvider`, and optionally your untrusted `Message-ID`.

**Three things I need you to know regardless of the signature you pick:**

1. **`DeliveryAttemptOutcome.InDoubt` is reachable and must not be treated as a settle.** I produce it
   whenever the end-of-data terminator was written and the acknowledgement was lost — the upstream
   may have accepted the message. My mapping is: 2xx → `Delivered`; 4xx → `TemporaryFailure`;
   5xx → `PermanentFailure`; connection/TLS/auth failure before commit → `TemporaryFailure`;
   terminator written, reply lost → **`InDoubt`**.
2. **I can return `PermanentFailure` for a recipient you sent me** (a 553/550, or an envelope
   address that is not a legal SMTP path). That is a per-recipient outcome on a message that may
   otherwise be fine — please don't collapse it into an item-level failure.
3. **I never batch recipients into one transaction**, so each result maps to exactly one recipient
   and no `Bcc` can leak to the upstream. If your worker expects to hand me `PendingRecipients` and
   get results back in the same order, that holds.

If your worker already exists against a different shape, **send me the signature and I'll implement
yours** — I have no attachment to mine and would rather match your call site than have you reshape
it. If you have not started the worker, the shape above is a concrete offer you can take as-is.

This is my only open item; everything else on my brief is done.
