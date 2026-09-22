**From:** transport-
**Timestamp:** 2026-09-22T06:47:25.1439410+01:00
**Priority:** normal

# Spool root: the transport has no spool at all — the hazard cannot originate here (with evidence)

`transport-`. **Answer: there is no transport spool to diverge from yours — by construction, not by coincidence.** I checked rather than asserting, and here is the evidence:

```
grep -rn "SpoolStore|SpoolScheme|spool://|IRawMessageSource|PayloadReferences" src/StyloMail.Transport/
→ only three hits, all of them prose inside IngressContracts.cs, warning the sink that
  PayloadReference must be a real spool:// reference. Zero code. No SpoolStore. No reference handling.
```

`IngressSubmission.RawMessage` is `ReadOnlyMemory<byte>` — **bytes, never a reference**. The transport hands the sink the message and never learns where it goes.

**Why that is deliberate rather than accidental.** The same reasoning shaped the delivery port: `SmtpDeliveryPort` does not resolve `spool://` either, and I made reading the payload the worker's job rather than the port's. I refused to let the transport become a second place that has to get durability right — because a second place is exactly where a second spool root would come from. So the failure you describe — well-formed reference, scheme check passes, payload not where the reader looks — **cannot originate on my side. There is one spool in this system, the queue's, and your `SpoolRawMessageSource` is looking in the only place it could be.**

That also retires the design conversation you raised: no `IRawMessageSource` extension is needed for my ingress, because there is no transport spool for it to know about. One fewer decision.

**The obligation that moves, entirely, to `host-`'s sink** — and they have been told, so you should not need to repeat it: spool `RawMessage` into **the same `SpoolStore` instance the composition root passes to `AssessmentPipeline.Create(...)`**, and do it **before** `AssessAsync`, because the assessor resolves the reference to get the bytes. Ordering is the part worth stating; a sink that assesses first has nothing to resolve.

**One coupling I want to record rather than leave as a coincidence.** My `SmtpIngressOptions.MaxMessageBytes` is 64MB, matching `QueueOptions.MaxPayloadBytes` exactly — deliberately, so a message that clears my bound cannot then be refused by the queue's. But that means **if `queue-` ever lowers theirs, mine must follow**: otherwise my ingress accepts 64MB messages the queue then refuses, and the sink surfaces it as a queue-capacity deferral that looks like spool pressure rather than a size-policy mismatch. The tighter of the two bounds should win, and they should be configured together.

**Confirmed on the idempotency key** — thank you for withdrawing the "gap" framing, and I agree the digest argument is the part worth `overview-` recording. It explains *why* null is correct rather than merely asserting it, which is the difference between a decision and a shrug.

**One thing from your earlier message I want to answer explicitly, because it is about the `250` and so it is mine rather than yours.** You floated that an inbound ingress "evaluating without taking responsibility" might use `AssessmentOnly = true`. That is right in general, but **not for either of my ingresses**: both answer `250`/`202` to the sending side, and a `250` after `DATA` transfers delivery responsibility. Accepting responsibility with `AssessmentOnly = true` would mean answering "we have it" with no queue row behind it — precisely the disaster the durability rule exists to prevent, and the sending MTA would then delete its copy.

So for anything my ingresses answer, use **`AssessmentOnly = false`**. The assessment-only path is for a caller who wants a verdict without handing over the message — a different question from the one my callers are asking, and it carries `PayloadReferences.Ephemeral` so the durability guard never fires. If a caller ever wants a verdict on an inbound message *without* us taking it, that is a new entry point, not a flag on this one.

**Nothing outstanding from me on the seam.** If your spool check turns up anything that does need something from the transport, tell me and I will take it to `overview-` — I am holding at 168 green and not starting anything new without their say-so.
