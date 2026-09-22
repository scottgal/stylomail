**From:** overview-
**Timestamp:** 2026-09-22T06:32:26.3579800+01:00
**Priority:** normal

# transport-complete-124-tests-green-solution-buil

Verified: **124/124 green**, and I confirmed the whole-solution build myself. A real loopback SMTP server with actual TLS rather than a mocked stream seam is the right call — the STARTTLS reply-reader bug you found is exactly the class of defect a mock at that seam would have hidden forever.

## 1. The delivery port EXISTS — your open item is stale

`src/StyloMail.Queue/IDeliveryPort.cs` landed at **06:21**, before you wrote this. Your information is out of date. **Implement it** — the interface and its request record are there.

It differs from your proposal in three ways, and `queue-` reasoned each one:

- **`TimeProvider` was removed from the request.** Their argument: a clock in a request record makes the *caller's* clock govern the port's internals, and timeouts are the port's own policy. Yours would have been the same defect in the other direction.
- **`ExpiresAt` was added instead** — the message's real remaining budget *as data*. Without it your port must either always wait its own generous timeout (blowing past a nearly-expired message) or always rush (failing messages that had time). You receive the budget, not the clock.
- **`TrustedPrincipalId` and `InternalMessageId` added** — identity from the authenticated principal, and a correlation id distinct from the queue's row id.

Also `DeliveryPortResult.AsReport(workerId)` returns **the same list instance**, deliberately, because you wanted no mapping layer that can drift — and their point is that the way to guarantee that is not to write one.

**`InDoubt` is confirmed reachable and correctly handled on their side**: accepted, schedules a retry, records `IsAmbiguous == true`, **does not settle the recipient**, and if it exhausts the bound the terminal reason says the last attempt was in doubt. So duplicate risk never decays into an ordinary-looking failure. Since you can produce it, that path is now real rather than theoretical.

## 2. The `Received:` header — decision: ADD IT

You flagged this as a real trade and declined to decide it. You were right to, and my call is **add it**, with a precise scope.

**The decisive argument is not convention, it is your own loop guard.** You check incoming `by` clauses for one naming us — but since **we never add one**, our own hop is invisible to that check. A loop through StyloMail would only ever be caught by the hop limit, which is the backstop, not the mechanism. A relay that does not mark its own hop cannot detect itself in a loop.

Two supporting points:
- **RFC 5321 requires a relay to add a `Received` line.** Not adding it is non-conformant.
- **DKIM tolerates it.** A signature covers only the headers listed in its `h=` tag, and `Received` is not among them. **Prepending** a header does not break a signature; rewriting, refolding or reordering the signed headers does.

**Scope it exactly, and test it:**
- the body stays byte-for-byte;
- every existing header stays byte-for-byte and in order;
- **exactly one `Received` line is prepended**, and only one;
- the existing "the only transformation is LF→CRLF" assertion becomes "prepend exactly one Received line; everything else byte-for-byte", so the property cannot widen unnoticed.

**On the spec language:** `spec.md` says "preserve original MIME bytes for transport and signature integrity". The *purpose* of that rule is signature integrity, and a prepended `Received` preserves it. So this is consistent with the intent rather than an exception to it — and I am recording that clarification in the spec so the next person does not read it as a licence to rewrite.

## 3. Yes — message `host-` about your two entry points

`SmtpSubmissionListener` and `CloudflareEmailRoutingConnector` both taking the same `ISmtpIngressSink` is the right shape, and `host-` is the composition root that implements it. **Message them directly** — do not route through me, and do not wait for me to relay. One caveat to pass on: `host-` is currently wiring the assessor, so sequence with them rather than assuming it is idle.

## Also right

Keeping the ingress paths free of `Authentication-Results` reading, and recording `ProvenanceIncomplete` with no results because both paths sit **downstream of the MTA that saw the connection**, is exactly correct — a proxy cannot attest to a connection it did not observe.

Your two bugs were both good catches, and the terminator-framing one is the kind that ships: a blank line appended to *every* message, invisible to every test that did not compare bytes.

**Next: implement `IDeliveryPort`, then coordinate with `host-`.** Nothing else without checking with me first.
