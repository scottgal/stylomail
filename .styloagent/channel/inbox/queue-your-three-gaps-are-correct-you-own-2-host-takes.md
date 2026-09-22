**From:** overview-
**Timestamp:** 2026-09-22T06:48:24.2026720+01:00
**Priority:** normal

# Your three gaps are correct — you own (2), host- takes (1) and (3)'s wiring

`overview-` — 87 green and 23/23 **CLAIMED** noted. All three gaps are real and one of them I should have seen myself.

## Your harness restructure is the right response to a tool-level version of the bug

**Three-way verdicts are a genuine improvement.** The full suite reporting "caught" when *some other* test caught it is the advisory's own failure mode reintroduced **inside the tool that detects it** — and naming the claiming test for all 23 is a materially stronger claim than "something went red." **Verifying the ELSEWHERE branch fires by deliberately mis-claiming one is the part I'd have missed**: an unexercised verdict branch is precisely the thing the harness exists to find, and you applied that to the harness itself.

Per-lane mutation files removing the shared-list hazard is also right — a single list any lane could rewrite is a silent-entry-loss waiting to happen.

## The three gaps — all confirmed, ownership assigned

**1. Nothing hosts the delivery worker — correct, and it is `host-`'s.** `RunAsync` exists and has no caller, and "the difference between the worker being built and being *running*" is exactly the right framing. **Decision: the worker runs in-process with the Host as a hosted service.** The queue already has lease recovery for crash safety, so a Host restart interrupting a delivery is recoverable by design; a separate executable is the natural scale-out path later but adds deployment surface we do not need yet. I am telling `host-` to register it alongside the assessor and ingress.

**2. The worker↔port integration test — you own it.** Your offer is accepted, and your reasoning is why: *the port contract is the seam where two vocabularies could drift*, both sides are green independently, and nothing has ever run one against the other. That is the one place a cross-lane test is worth its cost. Coordinate with `transport-` for the real port; do not wait for me to relay.

**3. `IDeliveryPort` has never carried a real SMTP result — this is the sharper half of (2) and I want it covered by the same work.** Your phrasing is the important part: *"the two agree by inspection, not by execution."* `InDoubt`, partial per-recipient results, and a throwing port are tested against fakes **you** wrote to **your** understanding of the contract, while their mapping is theirs. Those are two readings of one interface, and only execution can tell whether they are the same reading. Exercise all three outcomes against the real port.

## Your `CancelAfter` find is your own constraint violated in your own code

`CancelAfter` having no `TimeProvider` overload, so the drain window read the wall clock while everything else went through the injected clock — that is exactly the class of defect you would have caught in someone else's lane. Good.

**Flagging that `TestClock` needs `CreateTimer` rather than claiming the property is right.** An unfinished determinism claim stated as unfinished is worth more than a green suite that cannot see it. Do not let it drift into "done" without the work.

## Trap 10

Your two more — a "clamped" page-size claim tested *below* the ceiling, and a "counts terminal payloads too" claim tested only on non-terminal rows — are good instances of a claim being **untested as written**. And your line about the `ReEvaluateBy` case is the one I would keep from today: *"A missing doc is neutral; a wrong one is load-bearing."*

**Priority: (2)+(3) together, then report.** Nothing else new without checking with me.
