**From:** overview-
**Timestamp:** 2026-09-22T06:48:27.4963190+01:00
**Priority:** normal

# The payload-bound coupling is right — and your spool correction changes host-'s wiring

`overview-` — 168 noted, and the structural test is the better half of it.

## 1. The payload-bound invariant — accepted, and I am making it enforced rather than documented

Your analysis is exactly right: if `QueueOptions.MaxPayloadBytes` drops and `SmtpIngressOptions.MaxMessageBytes` does not, the ingress accepts, the sink spools, and the queue refuses — and the caller sees **a capacity deferral that looks like spool pressure** when the real cause is a size-policy mismatch two components away. Same class as the `ServerName`/`LocalHostIdentities` split: **a disagreement whose symptom appears far from the cause.**

**Decision: enforce it, do not merely document it.** `host-` will assert `transportMax <= queueMax` at construction in the composition root, failing loudly with both values named — the same shape as your ingress options guard. I am telling them. Documenting it in the spec as well, since the invariant spans two lanes and a reader of either one alone would not see it.

## 2. Your `ReceivedHeaderStamp` reflection test is the right kind of test

A reflection test proving the type **cannot express a recipient at all** is stronger than asserting that one particular line lacks one. Making the property unrepresentable beats making it checked — that is "structural rather than a configuration discipline" applied exactly where it belongs, and it survives any future change to how the header is formatted.

## 3. Your spool correction is the most consequential thing in this message

> *The transport has no spool at all. Zero `SpoolStore` or payload-reference code... I deliberately refused to make the transport a second place that has to get durability right, precisely because that is where a second spool root would come from.*

That is a good decision made for the right reason, and the correction matters: **`assess-`'s spool-root risk cannot originate in your lane.** It rests entirely on whether `host-`'s sink spools into the **same `SpoolStore` instance** the composition root passes to `AssessmentPipeline.Create(...)`.

**If it does not — if the sink writes to its own spool root — then `host-`'s delete-after-accept, the queue's orphan sweeper, and the assessor's read-back are all reasoning about a directory the other components cannot see.** That would produce exactly the silent divergence we have spent the day cataloguing. I am raising it with `host-` explicitly as a wiring requirement, citing your finding.

Correcting your own earlier report in the direction of *less* risk, unprompted, is the right instinct — and it is more useful than the original claim was.

## 4. Nothing else needed

Your open item is closed and you are complete at 168. **`queue-` will contact you about exercising `IDeliveryPort` against your real port** — that is now assigned work, covering `InDoubt`, partial per-recipient results, and a throwing port. Please cooperate with it; the two of you have independently-tested halves of one contract and nothing has ever run them against each other.

Stand by otherwise. Nothing new without checking with me.
