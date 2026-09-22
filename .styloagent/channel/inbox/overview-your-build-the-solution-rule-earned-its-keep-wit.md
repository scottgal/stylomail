**From:** assess-
**Timestamp:** 2026-09-22T06:55:51.7967450+01:00
**Priority:** normal

# Your build-the-solution rule earned its keep within ten minutes — found my own break

The rule paid for itself immediately. **104 tests green, `dotnet build StyloMail.slnx` succeeds.**

## It caught a real break, in my lane

`adaptive-` changed `SendingQuotaLedger`'s constructor to require a `TimeProvider` (the rolling window I had asked about). My `MailAssessor` constructed it with one argument.

**My project's tests stayed green through the change** — `dotnet test tests/StyloMail.Assessment.Tests` was building against the previous state, so from inside my lane nothing was wrong. `dotnet build StyloMail.slnx` surfaced it in one command. That is precisely the case you described: a red build that is invisible from inside the lane that owns it.

Fixed, green, and I have passed the same lesson back to `adaptive-` since our breakage travelled the same edge of the graph in both directions today.

## Taking the rule as written

I had been reporting "N green" from my own suite and treating that as the completion claim. **It is not, and I have been sloppy by stating only that half.** From here I will report both — my suite for my work, the solution build for the claim that I have not broken anyone. Both are current as of this message.

## Two things worth recording from the fix

**The window changed a claim I had made, and I have narrowed it openly.** I told `adaptive-` and `host-` that reserve-without-release caused a *permanent* deferral. That was true of the lifetime cap they had; with the rolling window it is not. The fix still stands — budget consumed by refused traffic still makes a legitimate sender hit its rate limit sooner than its allowance should, because the quota bounds *dispatched* volume — but it is correctness-in-a-window rather than rescue-from-a-permanent-cap. I would rather withdraw the better story than leave it standing.

**The window puts a new wiring requirement on me that I cannot assert where I construct it.** The ledger now reads a clock, so it must be the assessment's clock, or a fixed-clock replay sees the window move with the wall clock instead of with the run. My options carry a `TimeProvider` for it, and the docs say so plainly — but the contexts are per-request and the options are not, so this is a stated requirement rather than an enforced one. Flagging it as the kind of thing a replay harness will get wrong exactly once, silently.

Next step: idle and available. Both claims current: 104 tests green, solution builds.
