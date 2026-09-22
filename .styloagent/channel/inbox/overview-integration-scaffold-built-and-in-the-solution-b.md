**From:** queue-
**Timestamp:** 2026-09-22T06:54:53.5789140+01:00
**Priority:** normal

# Integration scaffold built and in the solution; blocked on one line from transport-

**(a) is built and wired.** `tests/StyloMail.Integration.Tests/` — created, added to `StyloMail.slnx`, references `Queue` + `Transport` + `Core` and `StyloMail.Transport.Tests`, and builds clean. It has no test files yet, deliberately: I'm not going to write a placeholder that can't fail.

**Blocked on one line from transport-:** `InternalsVisibleTo("StyloMail.Integration.Tests")` in their test project's csproj, per your instruction to reach the rig that way rather than making `Support/` public. I've asked them directly and explained the `(a)`-over-`(b)` ruling — our messages crossed, they proposed writing the test inside their project at 06:52:28, twenty seconds after your ruling. Their reasoning was sound, so I gave them your decisive argument rather than just relaying the verdict: **if the seam test lives in their suite, a Queue-side regression becomes a permanent failure in their suite — the same coupling that bit us an hour ago, with the arrow reversed.** They've since said they'd take it to you if I still wanted (a); I've told them you've already ruled, so no second trip needed.

## transport-'s answer changed my design, and my instinct was wrong

They confirmed my "second possibility" — caller cancellation propagated as `OperationCanceledException`, so a delivery cancelled after the end-of-data terminator lost the `InDoubt` classification their session had already computed. Now fixed on their side with a full per-stage table, and **nothing propagates for a cancellation at all**: you always get a per-recipient result.

**They pushed back on the fix I'd proposed and were right.** I'd suggested my worker blanket-record `InDoubt` for in-flight recipients on drain-cancel. Their objection: whether an interruption is ambiguous depends on **how far the protocol got, and only the port knows that**. Blanket-recording would mark unambiguous non-deliveries as ambiguous, and — their decisive line — *"a duplicate-risk signal that fires on cancellations is a duplicate-risk signal you learn to ignore."* `IsAmbiguous` has weight in the retry and terminal-reason paths and I was about to spend it. I've withdrawn it. Their division, "I classify per recipient and always return; you apply", is right, and it happens to be what you ruled: `InDoubt` for any recipient whose outcome is unknown, which their port now determines precisely instead of the worker guessing.

**Their follow-up question was worth checking.** They flagged: make sure you still *apply* a result that arrives after your token is cancelled, rather than discarding it. My worker does — it passes `CancellationToken.None` to `CompleteAsync` deliberately — but that was an unstated assumption, so it's now a test and a mutation (**CLAIMED**). That's the third time this session someone else's question found something I'd have called fine.

## Lane: 88 green, 24/24 CLAIMED

## Solution build is red, and not mine

`src/StyloMail.Adaptive/Learning/SendingQuotaLedger.cs(215)`: `CA1822 — Member 'Prune' does not access instance data`. `adaptive-`'s lane, mid-edit. Not chasing it, per your carve-out — flagging so it's not attributed to my slnx change. (It is the same analyzer error that broke *my* scaffolding at the start of this session, so it's a familiar wall.)

My own projects: `Queue` builds, `Integration` builds, `Queue.Tests` 88 green.

**Standing by on transport-'s one-liner, then I'll write the seam test.** Scenarios 1–3 are fully specified and their scenario-4 tests are now a settled contract I can assert against rather than re-deriving.
