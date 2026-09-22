**From:** ingress-
**Timestamp:** 2026-09-22T08:02:59.2113470+01:00
**Priority:** normal

# Yes to the Cloudflare route — bearer header, new env key. And the sink design is exactly right.

COMPLETED — the route is built, tested and probed live. Long reply because two of the items carry
decisions you should see.

ACTION: `POST /v1/ingress/cloudflare`, mapped only when `CloudflareIngress:Enabled`. Bearer header
(`Authorization: Bearer <secret>`), secret read from `STYLOMAIL_CF_INGRESS_SECRET` via a new constant
on `HostCredentials` beside the other two names, with a pure `RequireCloudflareIngressSecretIfEnabled`
guard. Raw RFC 5322 body, envelope in `X-StyloMail-Envelope-To` / `-From` headers, JSON reply.

RESULT: solution 0 errors / 0 warnings; **145 Host tests** green (15 new on this route alone); 0
failures across 15 tree-verified-clean runs.

**Design decisions inside the route, since they are not all obvious:**
- **No `SharedSecret` config property.** I removed the one I had put on `HostTransportOptions` — a
  secret with a config key is a secret that ends up in an appsettings file and then a repository, and
  the point of the constant is that the name is defined once and the value never written down.
- **The body is raw bytes, not a JSON envelope with Base64.** The Worker already holds the bytes; the
  envelope is in headers so an address needs no URL encoding and cannot be silently mangled by one.
- **Kestrel's body limit is raised to the connector's own maximum for this endpoint.** Its default is
  30 MB against the connector's 64 MB, so without this a message between the two would be refused by
  the server with a 413 that names no component and reads like the ingress's own limit.

**THE PROBE EARNED ITS KEEP HERE, and it is the reason I am confident in that last point.**
`WebApplicationFactory` runs `TestServer`, which **does not enforce a request body limit** — so the
test suite physically cannot tell whether that filter works. I verified it against a real Kestrel
process: a **32 MB body returned 503, not 413**, i.e. it passed the server's 30 MB default. That check
exists only because the earlier probe taught me not to trust a green suite on a wiring claim.

**Probe: 31/31 checks.** Wrong secret → 401, no secret → 401, authenticated Worker to an unserved
domain → 403 (the second gate, which is what still holds if the secret leaks), served domain → not
refused, route absent when disabled → 404, over-maximum body refused, **no oversized payload reached
the spool**, and the ingress copy is byte-identical to the Worker's bytes after exactly one `Received`
line.

TWO THINGS FOR YOU TO DECIDE, both measured rather than argued:

1. **The measured peak, which was one of the two open questions.** Because every message is spooled
   *before* it is assessed, a message that is then deferred or refused leaves its bytes on disk with
   no queue row referencing them — and `MaxLivePayloadBytesPerTenant` is computed from
   `queue_item.payload_bytes`, so those bytes are bounded by **nothing**. Measured live: **32.0 MB on
   disk across 2 payloads with 0 queue rows accounting for it**, reclaimed only by the orphan sweep
   after 1 hour. So an inbound flood of deferring messages has a rate × 1h × max-size ceiling that no
   configured bound accounts for. Trivially produced whenever the assessor is unavailable. I have not
   started any deletion — the peak question is what gated it, and this is the measurement.
2. **`MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` still carry doc comments saying the null
   sender "may be, as in a bounce", which is now stale on both** after the ruling. Not mine to edit;
   worth someone doing before the comment misleads the next reader the way they misled me.

Also, minor and yours: the issues list appears to have no close operation on the agent side. I filed
the `SmtpSubmissionListener` defect, `transport-` fixed and mutation-verified it, and neither of us
can mark it resolved — the thread reply is the only record. If issues are meant to be tracked to
completion rather than to filing, that path is missing.

Cloudflare route was the last outstanding item; I have nothing else in flight.
