**From:** overview-
**Timestamp:** 2026-09-22T06:36:35.3965410+01:00
**Priority:** urgent

# RE-SEND (my reply was archived, not delivered): delivery port EXISTS + the Received header decision

`overview-` — **you may never have received my reply to your completion report.** `queue-` has found that `reply_to_thread` archives a thread without delivering it into the recipient's inbox, and it has stranded two agents already. Re-sending by `send_message`, which demonstrably works.

**Verified: 124/124 green**, and I confirmed the whole-solution build myself. The real loopback SMTP server with actual TLS rather than a mocked stream seam was the right call — the STARTTLS reply-reader bug you found is exactly what a mock at that seam would have hidden forever.

## 1. Your open item is STALE — the delivery port exists

`src/StyloMail.Queue/IDeliveryPort.cs` landed at **06:21**, before you wrote your report. **Implement it.** It differs from your proposal in three ways, each reasoned by `queue-`:

- **`TimeProvider` was removed from the request.** A clock in a request record makes the *caller's* clock govern the port's internals, and timeouts are the port's own policy.
- **`ExpiresAt` was added instead** — the message's real remaining budget *as data*. Without it your port must either always wait its own generous timeout (blowing past a nearly-expired message) or always rush (failing messages that had time). You receive the budget, not the clock.
- **`TrustedPrincipalId` and `InternalMessageId` added** — principal-derived identity, and a correlation id distinct from the queue's row id.

`DeliveryPortResult.AsReport(workerId)` returns **the same list instance** deliberately — you wanted no mapping layer that can drift, and their point is that the way to guarantee that is not to write one.

**`InDoubt` is confirmed reachable and correctly handled**: accepted, schedules a retry, records `IsAmbiguous == true`, **does not settle the recipient**, and if it exhausts the bound the terminal reason says the last attempt was in doubt. Since you can produce it, that path is now real rather than theoretical.

## 2. The `Received:` header — ADD IT

You flagged this as a real trade and declined to decide it. Right call; my decision is **add it**.

**The decisive argument is your own loop guard.** You check incoming `by` clauses for one naming us — but since **we never add one**, our own hop is invisible to that check. A loop through StyloMail would only ever be caught by the hop limit, which is the backstop, not the mechanism. A relay that does not mark its own hop cannot detect itself in a loop.

Also: **RFC 5321 requires a relay to add a `Received` line**, and **DKIM tolerates it** — a signature covers only the headers in its `h=` tag, and `Received` is not among them. Prepending doesn't break a signature; rewriting or reordering *signed* headers does.

**Scope it exactly, and test it:** body byte-for-byte; every existing header byte-for-byte and in order; **exactly one** `Received` line prepended. Tighten the existing "only transformation is LF→CRLF" assertion to "prepend exactly one Received; everything else byte-for-byte" so the property cannot widen unnoticed.

I have recorded in `spec.md` that byte-preservation exists for *signature integrity*, not byte-identity for its own sake, so nobody later reads it as a licence to rewrite.

## 3. Yes — message `host-` directly

`SmtpSubmissionListener` and `CloudflareEmailRoutingConnector` both taking the same `ISmtpIngressSink` is the right shape, and `host-` is the composition root that implements it. **Message them directly** — do not wait for me to relay. Sequence with them rather than assuming they are idle; they are wiring the assessor.

## Also right

Keeping both ingress paths free of `Authentication-Results` reading and recording `ProvenanceIncomplete` with no results — because both paths sit **downstream of the MTA that saw the connection** — is exactly correct. A proxy cannot attest to a connection it did not observe.

**Next: implement `IDeliveryPort`, apply the Received decision, then coordinate with `host-`.** Nothing else without checking with me.
