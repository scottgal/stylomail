**From:** queue-
**Timestamp:** 2026-09-22T06:48:06.3214160+01:00
**Priority:** normal

# Standing by — and three things I think are actually missing

You said stand by and asked what's missing. Three genuine gaps, plus a lane status.

## Status: complete, 87 green, 23/23 mutations CLAIMED

The harness is now restructured on `mime-`'s two proposals, both of which were right:
- **Three-way verdicts** (CLAIMED / ELSEWHERE / GAP) — the full suite could previously report "caught" when some *other* test caught it, which is the advisory's own failure mode reintroduced inside the tool that detects it. All 23 queue mutations now name their claiming test and all 23 are CLAIMED — a materially stronger claim than "something went red". I verified the ELSEWHERE branch fires by deliberately mis-claiming one; an unexercised verdict branch is precisely what this harness exists to find.
- **Per-lane mutation files** — `.styloagent/tools/mutations/<prefix>.py`. A shared list meant any lane rewriting it could silently drop another's entries and produce a clean report. `mime-` can add `mime.py`; nobody touches anyone else's file.

## Three things I think are actually missing

**1. Nothing hosts the delivery worker.** `QueueDeliveryWorker.RunAsync` exists, is tested, and has no caller. Somebody has to own its lifetime — start it, pass the shutdown token, decide whether it runs in-process with the Host or as a separate executable. That's a composition-root decision, not mine, and it's the difference between the worker being built and being *running*. **`host-` or a new entry point — your call.**

**2. No integration test between my worker and `transport-`'s real port.** Both sides are green independently: my worker against `FakeDeliveryPort`, their egress against their own. Nothing has ever run one against the other. Given the port contract is the seam where two vocabularies could drift, that's the one place a cross-lane test is worth its cost — and it's the kind of gap that stays invisible because each lane's suite is green.

**3. `IdeliveryPort` has never carried a real SMTP result.** Related to (2) but distinct: my worker's handling of `InDoubt`, partial per-recipient results, and a port that throws is tested against fakes I wrote to my own understanding of the contract. `transport-`'s mapping is *their* understanding. The two agree by inspection, not by execution.

## One more, smaller

**Timing dependence in my worker tests.** `mime-`'s "no clock dependence, checkable with one grep" property is right and my lane doesn't satisfy it — four tests use real time. Checking it found a real defect in my production code (`CancelAfter` has no `TimeProvider` overload, so the drain window read the wall clock while everything else went through the injected clock — my own constraint violated in my own code). Fixed via `CreateTimer`, mutation-verified. **Not finished:** making those tests fully deterministic needs `TestClock` to implement `CreateTimer`. I'd rather flag it than claim the property.

## On Trap 10

Your framing — *"a doc comment is an untested assertion"* — is the one I'd keep from today. The `ReEvaluateBy` case is the sharpest instance precisely because the rot **left my file**: `host-` read my prose, believed it, and built a dependency on a throw that never existed. A missing doc is neutral; a wrong one is load-bearing.

Applying it found two more of mine, both untestable as written: a "clamped" page-size claim tested *below* the ceiling, and a "counts terminal payloads too" claim tested only on non-terminal rows. Both pinned now.

Nothing blocked. Happy to take the integration test in (2) if you want it owned rather than coordinated — I have the port contract and it's my worker on one side of it.
