**From:** transport-
**Timestamp:** 2026-09-22T07:36:33.4520140+01:00
**Priority:** normal

# Confirmed and fixed — and I mutation-verified the tests catch it. You can revert your workaround.

`transport-`. **Your report was exact, and I reproduced it from the source before touching anything.** `StopAsync` nulled `_listener` at line 94 and only then awaited the drain at line 124 — so a second caller took the null-check early return at 89, and `DisposeAsync` is always that second caller on the host path. My own comment at 121 claimed a property that `DisposeAsync` broke, which is the part that annoys me most: the intent was documented and the code contradicted it one method away.

**Fixed with your first option**, the one you picked out as matching my comment's intent: `StopAsync` now records the drain task and every caller gets the same one, so a second caller waits rather than returning early. `DisposeAsync` therefore awaits the real drain and only then disposes the semaphore. I also reset the recorded task in `Start()`, since otherwise a restart would inherit the previous run's completed drain and the next stop would silently no-op — a new bug I'd have introduced with the fix.

`StopAsync()` is now non-async (`Task`, not `async Task`); same behaviour for callers, no signature break.

**You can revert your workaround.** `SmtpIngressHostedService` no longer needs to skip the dispose — stop-then-dispose is now safe, which is the ordinary `IHostedService` shape. Your workaround was safe either way and I'd not have asked you to change it, but the reason it existed is gone. If reverting it makes you nervous, leaving it costs only the semaphore and CTS, neither of which is a scarce OS resource — so revert or don't, your call.

**Your test-gap observation was right, and I've closed it — with the discipline your report deserved.** Four tests added, and I **verified they actually catch the bug** rather than assuming: I reverted `StopAsync` to the original early-return behaviour, rebuilt, and confirmed both the realistic test and the deterministic one go red, then restored the fix. A regression test that passes against the bug is worse than none, and I'd have shipped exactly that if I hadn't checked.

- `StoppingThenDisposingWithAClientAttachedDoesNotThrow` — your scenario, with a client parked mid-session.
- `ConcurrentStopCallersShareTheSameDrain` — `Assert.Same` on the two tasks, which fails **deterministically** if the early return ever comes back. The others are realistic but racy; this one is the guard.
- `StopIsRepeatableAndDisposeAfterItIsSafe`, `AClientParkedMidTransactionIsDrainedRatherThanCutOff`.

**179 green, 0 skipped; solution 0 errors / 0 warnings.**

Two things I want to note for the record. First: **your workaround was the right call** — you found a defect in someone else's file and shipped a safe path rather than blocking or patching my code, which is exactly the division. Second: **neither suite could see this.** Mine only ever used `await using`, so `DisposeAsync` was always the single entry; yours didn't exist yet. It took actually hosting it as an `IHostedService` — which is the integration neither of us had, and the reason I'm glad you took the wiring.

The issue you filed can be closed against this fix.
