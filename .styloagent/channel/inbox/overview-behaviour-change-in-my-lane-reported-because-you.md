**From:** transport-
**Timestamp:** 2026-09-22T06:52:35.3812780+01:00
**Priority:** low

# Behaviour change in my lane, reported because you asked to control what lands: cancellation no longer propagates

`transport-`. Flagging a semantic change to ship-able code, since you asked to control what lands — I judged this in-lane and did it, but you should know it happened and can tell me to revert.

**The change:** `SmtpDeliveryPort.DeliverAsync` no longer propagates `OperationCanceledException` for the caller's cancellation. It returns a normal `DeliveryPortResult` with one outcome per recipient.

**Why:** `queue-` asked what my port does when their drain window cancels the in-flight token. Checking, I found I did the worse of the two things they described — threw — and that on the one path where it mattered I was computing the right answer and then discarding it.

Their drain cancels a delivery that may be **fully on the wire and unanswered**: body and end-of-data terminator written, verdict outstanding. My session already classified that as `InDoubt` when a *connection* was lost, but on a *cancellation* it took a different branch and threw. So a genuinely ambiguous message became nothing at all — no per-recipient record, and the worker would only learn of it later as an item-level `LeaseExpired`, if the sweep ran.

Now: cancelled before the terminator → `TemporaryFailure` (nothing committed, unambiguously); cancelled after it → **`InDoubt`**; recipients not yet reached → `TemporaryFailure`. No exception, ever. Four new tests, including the after-terminator case, which needed a new knob on my fake server to create a window where the message is transmitted and unanswered.

**Why I judged it in-lane rather than asking first:** it does not change the contract — it makes the port *conform* to it. `IDeliveryPort` already says "should return a result for every recipient it was given; throwing is reserved for the unexpected." A caller cancellation isn't unexpected and isn't something I can't describe, so throwing was a conformance bug in my component. It's also squarely in service of the integration test you assigned. Tell me if you'd rather I had asked, and I'll hold differently next time.

**One thing I pushed back on, which you may want to weigh in on.** `queue-` proposed recording `InDoubt` for **all** in-flight recipients whenever their drain cancels a delivery — their reasoning being that "we cut it off mid-flight" is genuinely ambiguous. I told them not to, and I'd like you to check my reasoning rather than take it on trust:

Whether an interrupted delivery is ambiguous depends on **how far the protocol got**, which only the transport knows. Before the terminator, nothing was committed and a retry carries no duplicate risk; after it, the message may be accepted. Blanket-marking both as `InDoubt` would make `IsAmbiguous` mean "we were interrupted" rather than "this may be a duplicate" — and a duplicate-risk flag that fires on ordinary cancellations is one that gets learned away. The division I proposed is *I classify per recipient and always return; you apply.* With the fix above that now holds unconditionally, so they need no special case.

If you think that's wrong, say so and I'll change my position rather than leave a disagreement between two lanes.

**Also:** I added a direct `StyloMail.Queue` project reference to my **test** project so the seam test can drive the real `QueueDeliveryWorker` against the real port. Test project only, no source dependency added — `StyloMail.Transport` still references Queue and nothing else. The integration test itself will live in my test project with `queue-` writing the assertions, per their preference and mine; the alternative was a new integration project needing `InternalsVisibleTo` on my `Support/` rig, which is more infrastructure for a test only two lanes run.

**172 green.** Standing by again.
