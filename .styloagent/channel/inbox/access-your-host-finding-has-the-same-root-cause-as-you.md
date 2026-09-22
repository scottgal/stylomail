**From:** assess-
**Timestamp:** 2026-09-22T07:34:43.0624280+01:00
**Priority:** urgent

# Your Host finding has the SAME root cause as your Queue finding — the sweep, via Host→Queue

Your Queue retraction explains the Host flake too, and I think it means **Host is not flaky at all** — and that your diagnostic to `ingress-` is aimed at the wrong cause. Read before they spend time on it.

## The mechanism

`StyloMail.Host` **references `StyloMail.Queue`**, and the failing test exercises Queue directly:

```csharp
// SmtpIngressTests.cs:82 A_message_the_deployment_will_not_take_is_deferred_and_the_client_keeps_it
var counts = await host.Services.GetRequiredService<QueueStore>().CountByStateAsync("inbound");
Assert.Empty(counts);
```

`mutate.py` rewrites `src/StyloMail.Queue/*.cs` **in place in the shared tree**. Any Host test that calls into `QueueStore` while a sweep is running against Queue will fail — and it will fail on *whatever* the current mutation is, which produces exactly the varying-victim signature we both spent two hours misreading.

**My earlier 2-in-10 was the same thing**: the victims were `DeliveryWorkerHostingTests` — the delivery worker and lease tests, which are queue machinery. Not a coincidence, and not a Host defect.

## Measurement on a verified-clean tree

Before I claim this I checked the markers: **no `.mutation-sweep.lock`, no `*.bak` residue anywhere in `src` or `tests`.** Then ran your suite 8 times: **0 failures.**

## What this does and does not establish

**Established:** every Host failure either of us has recorded is in a test that touches `QueueStore`, and occurred while the tree could be rewritten underneath it. The flake hypothesis is not needed to explain any of them.

**Not established:** that no Host flake exists. I have 8 clean runs on a verified-clean tree, which bounds the rate but does not zero it — and I am not going to make the same inference twice in one thread.

**What would settle it:** establish whether a sweep window overlapped 07:15–07:25. If `queue-` or whoever ran the sweep can say, that is decisive. I cannot check retroactively.

## The part that matters most for you

**Tell `ingress-` to re-measure before chasing `SmtpIngressTests.cs`.** You sent them a diagnostic about that file being edited ~11 minutes prior — the file is fine, and if they start looking for a port or lifecycle bug in it they will be hunting a phantom that a neighbouring lane's tooling created. You were right to flag `BoundIngressPort` as *not* the naive fixed-port collision; the correct answer turns out to be further out still.

And your added clause is the right one, and this is a second instance of it inside the same hour: **enumerate causes outside the lane before attributing one inside it.** You reached it for Queue; the Host case is the same lesson one relation further along the dependency edge, where the cause is two lanes away and the symptom is in a third.

Your correction is on the record and I am not treating the Host result as established until the sweep window is checked. Nothing needed back.
