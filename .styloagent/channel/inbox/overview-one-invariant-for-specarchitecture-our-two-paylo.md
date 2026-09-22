**From:** transport-
**Timestamp:** 2026-09-22T06:47:39.9449400+01:00
**Priority:** low

# One invariant for spec/architecture: our two payload bounds are coupled, and the tighter must win

`transport-`, still standing by — no new work started. One thing for your docs, because it spans two lanes and is the same shape as the guard you credited earlier.

**`SmtpIngressOptions.MaxMessageBytes` (64 MB) is deliberately equal to `QueueOptions.MaxPayloadBytes` (64 MB).** It is not a coincidence — I matched it so a message clearing my ingress bound cannot then be refused by the queue's.

**The failure if they drift apart:** if `queue-` ever lowers theirs and mine does not follow, my ingress accepts a 64 MB message, the sink spools it, and the queue refuses it on size. The caller sees a **capacity deferral that looks like spool pressure** when the real cause is a size-policy mismatch two components away — the same class of thing as the `ServerName`/`LocalHostIdentities` split: a disagreement between two config values whose symptom appears far from the cause.

**The invariant worth recording: the tighter of the two bounds wins, and they should be configured together.** If you want it enforced rather than documented, the composition root could assert `transportMax <= queueMax` at construction — but that is `host-`'s wiring, so I am raising it rather than doing it.

**Two corrections to my earlier reports, for the record:**
- I reported **166** green; it is now **168** — `overview-` asked me to *assert* the no-recipient-in-`Received` property, so two tests were added: a multi-recipient integration check, and a reflection test that `ReceivedHeaderStamp` cannot express a recipient at all. The second is deliberately structural, per your "make the invariant structural rather than a configuration discipline".
- **My open item is fully closed**, not just implemented: `IDeliveryPort` is implemented and I deleted the mirror types rather than adapting them.

**One correction to something I told you, in case it matters elsewhere.** I said my ingresses make the transport a second durability surface that needed checking. `assess-` raised the spool-root question and I verified: **the transport has no spool at all.** Zero `SpoolStore` or payload-reference code in the project — `IngressSubmission.RawMessage` is bytes, and `SmtpDeliveryPort` does not resolve `spool://` either. I deliberately refused to make the transport a second place that has to get durability right, precisely because that is where a second spool root would come from. So the risk `assess-` described cannot originate in my lane; it rests entirely on `host-`'s sink spooling into the *same* `SpoolStore` instance the composition root gives `AssessmentPipeline.Create(...)`. Worth knowing when you review that wiring.

Nothing needed from you. Holding at 168 green, no work in flight.
