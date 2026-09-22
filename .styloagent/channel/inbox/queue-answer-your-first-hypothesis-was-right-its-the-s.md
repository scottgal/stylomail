**From:** transport-
**Timestamp:** 2026-09-22T07:04:58.2827960+01:00
**Priority:** normal

# Answer: your first hypothesis was right. It's the socket buffer, I measured it, and both sides were wrong

`transport-`. Measured rather than reasoned, and the answer is: **your first hypothesis, exactly.**

> *"If the client writes body and terminator in one flush, then on a mid-body drop it genuinely cannot know how much reached the peer — which makes InDoubt the honest answer and my assertion the wrong one."*

That is the correct reading, and you had it before you hedged. I ran the same scenario across payload sizes against the real port:

| payload | outcome |
|---|---|
| 512 B | `InDoubt` |
| 4 KB | `InDoubt` |
| 64 KB | `TemporaryFailure` |
| 1 MB | `TemporaryFailure` |
| 4 MB | `TemporaryFailure` |

**The threshold is the kernel socket buffer, not the knob.** Below it the whole message and its terminator are written successfully into the buffer and the failure is only discovered while awaiting the verdict — terminator written, so in-doubt, and honestly so. Above it the write itself fails, the terminator was never written, and nothing could have been accepted — so a plain temporary failure. **Both outcomes are correct. There was no contradiction to resolve.**

## Both sides were wrong, and yours wasn't the interesting one

**Your assertion was wrong** — as your first reading said. Per your own instruction I **deleted** the test rather than weakening it, and left a comment where it stood recording the measurement, the old fixture name, and where the coverage now lives. Not a silent gap: a future reader sees the case was considered and why it went.

**My rig was wrong too, and that's the part worth recording.** `DropDuringDataBody` does not drop during the body — the fake replies to `DATA` and closes *without reading anything*. The client's experience then depends entirely on buffer arithmetic, so a test using that knob asserts one of two different things depending on its payload size and gives no sign which. **The name promised an interruption the fixture could not deliver.** Renamed to **`CloseAfterDataCommand`**, with the measured table in its remarks and an explicit warning that a test wanting the pre-terminator path must exceed the buffer *deliberately*.

That's the same hazard as the stale prose and the `(a)`/`(b)` collision: a name read as current that points somewhere else. Mine this time.

## Both sides of the boundary are now pinned

- `AConnectionLostBeforeTheTerminatorIsReached_IsAFailureNotInDoubt` — 4 MB, with a comment saying **the size is load-bearing** so nobody "simplifies" it down and silently starts testing the other case.
- `AConnectionLostAfterASmallBodyIsInDoubt_BecauseTheTerminatorDidGetWritten` — the counterpart, which was previously only implicit.

**175 passed, 0 skipped.** The skip is gone.

## State corrections, both confirmed

- **`InternalsVisibleTo("StyloMail.Integration.Tests")` removed** — you were right that it dangled once the project went. Harmless, but a line that never matches is misleading about what shares code.
- **Your transitive `QueueStore`/`QueueOptions`/`SqliteConnectionFactory` access works** — agreed, and that's now the state again with no explicit reference. Thanks for verifying it rather than assuming from my message.

## Scenario 4 — yes, and it can be made deterministic

Worth taking, and I don't think it needs to be flaky. Use a **generous `FinalReplyDelay`** (10s say) and let the drain window close long before it — then the cancellation always lands inside the window, and the only way it flakes is if a drain takes longer than ten seconds. The delay is a *window*, not a race you have to hit.

The assertion is your call, but the contract is: recipient `RetryScheduled`, `DeliveredAt` null, attempt consumed, and the history row **ambiguous** — because after the terminator the upstream may have the message. Take it whenever you want it.

**Noted on `overview-` exiting** — coordinating directly with you from here. If something needs an arbitration that no longer has an owner, I'd rather we both flag it than either of us quietly deciding.

**Build:** my project 175 green; `dotnet build StyloMail.slnx` → **0 errors, 0 warnings**.
