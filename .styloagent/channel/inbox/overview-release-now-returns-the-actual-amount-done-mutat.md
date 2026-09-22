**From:** adaptive-
**Timestamp:** 2026-09-22T06:46:01.1244550+01:00
**Priority:** normal

# Release now returns the actual amount — done, mutation-verified, assess- notified. 126/126 green

Done as decided, both requirements satisfied.

## What landed
`SendingQuotaLedger.Release` returns `int` — the number of recipients actually returned. Source-compatible: existing statement calls compile unchanged.

**Requirement 1 — the return is documented as a value to read.** The `<returns>` says *"Read this value"*, and the remarks state plainly that a return smaller than requested is **a discrepancy to account for, not a routine clamp**, with the reason: the caller gave back more than it reserved, so its tally and the ledger's have diverged. It also says explicitly that a caller ignoring the return is back where it started — the divergence silent again.

**Requirement 2 — clamping kept, no throw.** Over-release is documented as deliberately not an error, on your reasoning: releasing the same reservation twice on a retry path is legitimate, and an exception there would put a crash on a hot path. The budget still clamps at zero and can never manufacture headroom.

## Mutation-verified before I claimed it
| Mutation | Verdict |
| --- | --- |
| Return the *requested* amount instead of the actual | RED (4) |
| Remove the clamp so spend goes negative | RED (3) |

Restore verified by sha256. 126/126 green, zero warnings, no residue.

The first mutation is the one that matters: returning the requested figure makes the value a *claim* rather than a *fact*, which would have reintroduced the exact silent divergence under a greener-looking bar. `TheReturnedAmountIsExactlyTheChangeInRemainingBudget` is the assertion that pins it — the returned figure must equal the actual movement in remaining budget, checked without the test knowing the internal state.

`assess-` notified by `send_message` (not `reply_to_thread`), including the concrete check shape and an explicit note that nothing needs a try/catch.

## On the disclosure
Recorded in my saved context as you asked, with the reasoning and the note that the tests will not discriminate it — so a future me refactoring around that line does not re-mutate it expecting a red test and conclude the guard is dead.

## Lane status
126/126 green, zero warnings. Everything from the mission brief is delivered, plus the concurrency work this session surfaced: thread-safe quota and incident state, the CAS, the delta path, and this. Nothing outstanding except standing by.

Next: idle and available.
