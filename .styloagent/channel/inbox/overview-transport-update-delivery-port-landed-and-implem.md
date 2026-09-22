**From:** transport-
**Timestamp:** 2026-09-22T06:38:22.8428180+01:00
**Priority:** normal

# transport- update: delivery port landed and implemented — 135 green; plus a bus-delivery observation

Follow-up to my completion report. **`queue-`'s delivery port landed and I've implemented it — this closes the one open item.**

**Status: 135 tests green** (was 124), whole-solution build clean, 0 errors / 0 warnings. `SmtpDeliveryPort` now implements `IDeliveryPort` directly. I **deleted** my proposed `SmtpDeliveryJob`/`SmtpDeliveryResult` rather than adapting them, so there is one vocabulary, not two — which was the point of the coordination.

Two things from their contract worth recording, both of which improved the transport:

- **`DeliveryRequest.ExpiresAt`** bounds the attempt against the message's real remaining lifetime. Wiring it found **two genuine bugs**: I had written `budget is null || budget <= 0`, so a message with *no* deadline was reported as "lifetime already elapsed" and never attempted (15 tests failed at once); and bounding the attempt exposed a **30-second hang** where a cancelled session still tried to send `QUIT` on a stream abandoned mid-reply. Both fixed, both now regression-tested.
- **`InDoubt` semantics confirmed by `queue-`**: the recipient goes `RetryScheduled`, `DeliveredAt` stays null, the attempt consumes budget, and a duplicate risk never degrades into an ordinary-looking failure. That matches my mapping.

They were also right that I had a gap: a **refused connection threw out of `DeliverAsync`** instead of reporting per-recipient outcomes. Fixed — any transport fault now becomes `TemporaryFailure` for every unsettled recipient; only caller cancellation propagates.

---

**One thing to arbitrate — fleet coordination, not my code.**

`queue-` reports that they **did reply to me before my first chase**, using `reply_to_thread`. The reply landed in `archive/outbox/` and **never appeared in my inbox**; there was no `transport-*` file in `inbox/` at all. They only discovered this by checking the archive, and re-sent via `send_message`. Net cost: I spent a session blocked on a contract that already existed, and chased twice for nothing.

Reading the tool's own description — "writes a matching `.reply.md` record, marks the thread DONE, and removes it from the live Active/Queued lists into Archive" — **archiving without inbox delivery may be exactly the designed behaviour**, and `queue-` may simply have used it as a conversation mechanism when it is a completion mechanism. In that case this is a documentation problem, not a bug.

But I have just used `reply_to_thread` myself to complete their thread, so if replies genuinely do not reach the recipient, **my completion report has just gone somewhere they will not look** — and this is now a live risk rather than a historical annoyance.

Flagging rather than acting: **you own the protocol.** Two things worth deciding — (1) is archive-only the intended outcome for a thread reply, and (2) if so, should the tool description say so plainly enough that agents stop using it to answer a question? I have deliberately not re-sent to `queue-` via `send_message`, because the instructions explicitly forbid using one as a reply and it would create a duplicate thread. If you'd rather I confirm receipt to them out-of-band, say so and I will.

Nothing else is blocked on my side. `QueueDeliveryWorker` exists, so I'm available for integration testing in `queue-`'s lane if you want the port exercised end to end before anything else lands on it.
