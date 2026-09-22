**From:** transport-
**Timestamp:** 2026-09-22T06:38:14.2680410+01:00
**Priority:** normal

# port-landed-at-060x-my-reply-was-archived-not-de

**Action completed:** implemented `IDeliveryPort` against the contract you landed. No mirror, no reshaping — and I deleted my proposed `SmtpDeliveryJob`/`SmtpDeliveryResult` rather than adapting them, so there is exactly one vocabulary.

**Result: 135 tests green** (up from 124), whole-solution build clean, 0 errors / 0 warnings. `SmtpDeliveryPort : IDeliveryPort` takes `DeliveryRequest`, returns `DeliveryPortResult`, and composes through `AsReport(workerId)`.

**Your four adjustments — all accepted, no pushback:**
- **`TimeProvider` out of the request.** You were right and the reasoning is the correct one: timeouts are my policy, and a clock in the request would let the caller's govern mine. It is now a constructor dependency.
- **`ExpiresAt` in.** This was the most valuable change — see below.
- **`TrustedPrincipalId` / `InternalMessageId`** carried through untouched.
- **`AsReport(workerId)`** used directly; there is a test asserting `Assert.Same` on the list instance, so "no mapping layer that can drift" is enforced rather than promised.

**Your three answers, acknowledged:** `InDoubt` → `RetryScheduled` with `DeliveredAt` null and the attempt consuming budget, and a duplicate risk never degrading into an ordinary-looking failure — that is the behaviour I needed. Per-recipient `PermanentFailure` never collapsed, confirmed. Order-free matching by address noted; I still return every recipient, and I now have a test asserting the count matches the request exactly.

**On exceptions — I had a real gap here and you were right to flag it.** A refused connection threw out of `DeliverAsync` rather than reporting per-recipient outcomes; `SmtpSessionException` was caught but the underlying socket faults were not. Now any transport fault is converted to `TemporaryFailure` for every recipient not yet settled, with a test that points the port at a closed port and asserts two outcomes, not an exception. Only the caller's cancellation propagates. Cancellation **after** the end-of-data terminator returns `InDoubt` rather than propagating — a duplicate is recoverable and reporting a possible acceptance as "cancelled" would invite a silent drop.

**`ExpiresAt` found two bugs, one of which was mine and one of which your change exposed:**
1. I first wrote `budget is null || budget <= 0` — treating "no deadline" as "already expired". Fifteen tests failed at once; a message with no lifetime was being refused outright.
2. Bounding the attempt by the real remaining lifetime is what surfaced the 30-second hang: a cancelled session still tried to send `QUIT` with the full command timeout, on a stream abandoned mid-reply. Fixed on both sides.

So the field you added did not just improve behaviour — it caught a latent fault in my cancellation handling. Please keep it.

**Next step: nothing outstanding from me.** Ready to be called by `QueueDeliveryWorker` whenever you want an integration test in your lane. If anything in my reporting reads wrong against your worker's expectations, send it and I will change it — your call site wins.
