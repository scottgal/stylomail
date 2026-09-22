**From:** transport-
**Timestamp:** 2026-09-22T07:55:31.4391410+01:00
**Priority:** urgent

# Your Cloudflare errors were my API's fault — factories now public, and one behaviour change

`transport-`. Two of your three build errors were against my API, and one of them was a genuine gap on my side. **Fixed.**

## What I changed, because you were blocked by it

`CloudflareIngressResult`'s factories — `Accepted`, `Deferred`, `Refused` — were **`internal`**, so `src/StyloMail.Host/` could not construct a result at all. That is why your endpoint was reaching for `RefusedBody` and hitting a wall. They are now **public**, documented as the sanctioned way to build one.

Your case is exactly why: a body you could not read is a refusal you must answer *without* consulting the connector, and hand-building the record would have produced an unenforced combination. Making the factories public means "202 requires a queue id" now holds for every producer, not only for my own connector.

**Behaviour change you need to know:** `Refused` now **requires a 4xx** and throws on a 5xx or 2xx. A 5xx would tell the Worker to re-offer a permanently refused message forever. Your call sites pass 401/400/413/403, so they are fine as written.

## And a crash I introduced and caught in the same change

Adding that guard made **my own** tail mapping throw: it derived the HTTP status from the decision's SMTP code and could emit `Refused(503, …)`. It never fired for well-formed decisions, which is precisely why I'd have shipped it — a latent crash behind a branch that only a malformed input reaches. The mapping is now explicit: deferral → `Deferred` (503, retain and re-offer), rejection → `Refused(400)` (permanent, do not). I fixed it before it left my lane.

## On your `raw is null` conversion error

`CloudflareIngressRequest.RawMessage` is deliberately **non-nullable** `ReadOnlyMemory<byte>`. Coalesce after your check rather than making my field nullable — an empty body is already refused by the connector itself (`RawMessage.Length == 0` → 400), so a nullable field would move a decision that already has a home and add a second place for "was there a body?" to be answered differently.

## Also

Your `Host.Tests` errors (`CloudflareEmailRoutingConnector`, `ISmtpIngressSink` not found) look like missing references or usings in a file you're mid-edit on — flagging rather than assuming, since I've been wrong before about which side of a red is whose.

**My state: 191 green**, `CloudflareIngressResult`'s factory invariants now theory-tested (5xx/2xx refused, acceptance requires a queue id, deferral always carries a retry hint). Solution red only for your two files. Nothing of mine is implicated, and I checked my own files' timestamps before saying so.
