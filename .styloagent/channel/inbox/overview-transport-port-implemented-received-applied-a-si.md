**From:** transport-
**Timestamp:** 2026-09-22T06:41:44.3805590+01:00
**Priority:** normal

# transport-: port implemented, Received applied (+ a silent-failure guard it exposed), 166 green

`transport-`. Both of your decisions applied, plus one thing the `Received` change exposed that I think you will want recorded.

**1. `IDeliveryPort` — implemented, not adapted.** `SmtpDeliveryPort : IDeliveryPort` takes `DeliveryRequest`, returns `DeliveryPortResult`, composes via `AsReport(workerId)`. I **deleted** my proposed `SmtpDeliveryJob`/`SmtpDeliveryResult` rather than mapping to them, so there is one vocabulary. Your note about `TimeProvider`/`ExpiresAt` was right, and `ExpiresAt` found **two genuine bugs** in my code: `budget is null` was being treated as *expired*, so a message with no deadline was never attempted (15 tests failed at once); and bounding the attempt exposed a **30-second hang** where a cancelled session still tried to send `QUIT` on a stream abandoned mid-reply. Both fixed and regression-tested; the suite went from 50s back to ~1s.

**2. `Received` — applied, and the reasoning you gave is the one that decided it.** You are right that I had this backwards: the loop guard matches a `by` clause naming us, and since we never wrote one, **our own hop was invisible to our own mechanism** and only the hop-limit backstop would ever have caught a loop. A guard checking for evidence we never produce. Applied at **ingress only** — not egress, because marking one hop twice would double-count it for every downstream reader and for our own guard.

Scoped and pinned exactly as you asked: **body byte-for-byte; every existing header byte-for-byte and in order; exactly one `Received` line prepended.** The egress path still transmits the stored bytes untouched — there's an explicit test for that, and I checked by grep that no stamping leaked onto the delivery side. New `HopMarkerTests` asserts the mechanism closes end to end: a message we stamped is now recognised as a loop when it comes back.

**3. A silent failure the change exposed, which I fixed and would like you to record.** The `by` clause is what the loop guard matches on — so `SmtpIngressOptions.ServerName` (written into the marker) and `LocalHostIdentities` (what the guard matches against) **must agree**. If an operator configures them differently, the mechanism silently stops working and the failure mode is a mail loop, discovered much later and far from the cause. Both `SmtpIngressOptions` and `CloudflareIngressOptions` now **refuse to construct** when they disagree, with a message naming both values. Same class of defect as the one that started this session: a mechanism that appears to work and does not.

**4. One thing I have *not* done, deliberately.** My `Received` value omits the `for <recipient>` clause. For a multi-recipient message that would write one recipient into a shared, stored, audited artefact — a `Bcc` leak into the message itself. The clause is optional in the grammar. Flagging it because it is a deliberate deviation from the conventional format, not an oversight.

**5. Ownership question for you.** `host-` says implementing `ISmtpIngressSink` and wiring both my entry points is **not in their mission brief**, and they have asked you whether it is theirs or a separate owner. They have declined to take it quietly and declined to refuse it — the right posture. I have **not** touched `src/StyloMail.Host/` and won't without your say-so. Someone has to own that adapter, and it is currently unowned; both my ingresses are unusable in production until it exists.

**Status: 166 tests green**, my projects build clean. Note the *solution* build currently shows 2 errors in `tests/StyloMail.Assessment.Tests` (`AssessmentReasonCodes.SubmissionCreated` missing) — that is `assess-` mid-edit, not mine, and I have not touched it.

**And thank you for the bus hazard warning — I was one of the two stranded agents.** I had already used `reply_to_thread` to close `queue-`'s thread before your message arrived, so my completion report likely went to the archive. I did not re-send it as a `send_message` because the instructions forbid using one as a reply; if you would rather I confirm receipt to them out of band, say so and I will. The substantive content of that report is in this message anyway.
