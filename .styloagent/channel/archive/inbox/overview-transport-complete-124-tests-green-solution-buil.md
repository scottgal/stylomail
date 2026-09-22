**From:** transport-
**Timestamp:** 2026-09-22T06:30:55.9654290+01:00
**Priority:** normal

# transport- complete: 124 tests green, solution builds clean; one open contract with queue-

`transport-` reporting in. Everything on my brief is built and tested. **124 tests, all green**, verified with `dotnet test tests/StyloMail.Transport.Tests/...`; whole-solution build via `dotnet build StyloMail.slnx` is clean — no errors, no warnings, analyzers as errors. Not committed (`git add`/`commit` are forbidden for me).

## Files created
`src/StyloMail.Transport/` (19 files) — deps: **Core + Queue only, zero third-party packages.**
- `Smtp/` — `SmtpReply`, `SmtpReplyReader` (bounded line/reply reader), `SmtpDataWriter` (dot-stuffing + CRLF), `SmtpSession` (client state machine), `SmtpChannel`/`SocketSmtpChannel`, `SmtpBounds`, `SmtpUpstream`/`SmtpCredentials`, `SmtpTranscript` (redacting), result/exceptions.
- `Delivery/SmtpDeliveryPort.cs` — the queue's egress port.
- `Ingress/` — `SmtpSubmissionListener`, `SmtpIngressSession`, `SmtpIngressOptions`, `RecipientDomainPolicy`, `TransportHeaderScanner`, `IngressContracts`.
- `Cloudflare/CloudflareEmailRoutingConnector.cs` — inbound-only Worker ingest.

`tests/StyloMail.Transport.Tests/` (11 files) — including a loopback fake SMTP server with **real TLS** (self-signed cert), not mocks at the stream seam.

I also added both projects to `StyloMail.slnx` (append-only, low conflict risk) so the solution build covers me.

## The delivery-port shape I proposed to `queue-`
**Open item — I did not get a reply.** `queue-` has **no delivery port in `src/StyloMail.Queue` yet**, so `SmtpDeliveryPort` currently exposes a plain method rather than implementing a queue interface. What matters is that **it already speaks Queue's vocabulary** — it returns `IReadOnlyList<RecipientDeliveryResult>` with `DeliveryAttemptOutcome` — so there is no parallel vocabulary to unwind. Missing is only the *interface* and the *request record*:

```csharp
public interface IDeliveryPort
{
    ValueTask<DeliveryPortResult> DeliverAsync(DeliveryRequest request, CancellationToken ct);
}
// request: QueueId, TenantId, Direction, MailFrom, Recipients, Payload bytes, TimeProvider
```
When it lands, the work on my side is one small adapter file. **If `queue-`'s worker already exists against a different signature, theirs wins** — send it to me and I'll implement that instead. This is the one thing I need from them; I've chased once and will chase again.

One thing for their worker: **`DeliveryAttemptOutcome.InDoubt` must be reachable.** I can produce it, and it must not be treated as a settle.

## Hard constraints — evidence
1. **Durable-before-250** — the 250 is emitted from exactly one place, downstream of a decision that names a queue row. Tested: sink defers → **451, never 250**; sink *throws* → **451, never 250**. `IngressDecision.Accepted` cannot be constructed without a queue id, and `IsAcceptanceValid` catches a hand-built record that omits one (downgraded to a deferral by both consumers).
2. **Signed content preserved** — byte-for-byte round-trip asserted through a real socket; headers not refolded or reordered; dot-stuffing verified. The *only* transformation is LF→CRLF, which RFC 5321 requires and which is what *preserves* DKIM rather than breaking it. Stated as an exact test so it cannot widen.
3. **No bespoke bounces** — a permanent rejection is recorded; the session sends exactly one transaction and nothing back to `From`.
4. **Loop + hop limits** — `Received` count trips at the limit; a `by`-clause naming us trips the loop guard; a `for` clause naming us deliberately does **not** (that would refuse most of the internet).
5. **Authentication-Results trust** — nothing in either ingress path reads the header. Provenance is recorded `ProvenanceIncomplete` with no results, because both paths are downstream of the MTA that saw the connection.
6. **Bounds** — reply line bytes, reply lines, reply bytes, command bytes, message bytes, recipients, concurrent connections, commands/session, auth attempts, header bytes/count/line — each with a test that makes the bound actually fire.
7. **Injected `TimeProvider` throughout**; timeouts tested against `FakeTimeProvider`, not wall clock.

## Two bugs found and fixed en route (worth knowing)
- **A stale reply reader after STARTTLS.** The reader held the pre-TLS stream, so the "encrypted" session was reading TLS records as SMTP replies. It happened to pass one test and failed another — a genuine, timing-dependent defect that would have been intermittent in production.
- **Terminator framing.** I initially wrote `\r\n.\r\n`; since the body writer already guarantees a trailing CRLF, that appended a blank line to **every** message. Caught by the byte-preservation test.

## Deliberately not done
- **No `Received:` header added.** Preserving the original bytes wins over MTA convention, so our hop is not recorded in the message — loop detection leans on the `by` clause plus the hop limit. **Flagging it because it is a real trade**: if you want the stronger loop guard, the cost is modifying message bytes, which contradicts constraint 2. Your call, not mine.
- **No SMTP client library** (MailKit is in the cache and available). A hand-written engine is the only way to *guarantee* byte preservation and the InDoubt boundary. Deliberate rejection, revisitable.
- **No outbound signing.** The brief says signing belongs *after* any intentional modification; we make none, so there is nothing to sign. A TLS/transport signing layer would be new scope.
- **Not wired into `Host`.** That is `host-`'s project. Two entry points are ready for them: `SmtpSubmissionListener(options, ISmtpIngressSink, ISubmissionAuthenticator?)` and `CloudflareEmailRoutingConnector(options, ISmtpIngressSink)` — both take the **same** `ISmtpIngressSink`, which the composition root implements by running assessment and then `QueueStore.AcceptAsync`. I have not messaged `host-` yet; say the word or I will.

## Contract friction
None with Core or Queue as written — `QueueSubmission`/`RecipientDeliveryResult`/`DeliveryAttemptOutcome` fitted the transport's needs without change. The only gap is the missing delivery-port interface above.

Nothing is blocked on my side; I can start the `IDeliveryPort` adapter the moment `queue-` replies.
