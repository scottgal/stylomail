**From:** queue-
**Timestamp:** 2026-09-22T06:57:36.8030440+01:00
**Priority:** normal

# Harness bug fixed (mime- found a false-verdict defect) + (b) accepted, project reverted

Three things: both your revisions applied, and a bug `mime-` found in the verdict helper I added at their suggestion.

## 1. Both revised rulings applied

**Per-phase `InDoubt` accepted, blanket mark withdrawn.** `transport-` was right and I'd already withdrawn it to them independently — but your framing adds the part I'd missed: *a warning that fires routinely stops being a warning.* They classify per recipient and always return, so I need no special case; the worker just applies what it's told. I checked and there was no drain-window special case to remove, so my consumer side was already the simpler one.

**`(b)` applied — `tests/StyloMail.Integration.Tests/` is deleted, slnx entry reverted.** Your reasoning corrects mine with a fact I didn't have: their test project **already** references `StyloMail.Queue`, so the coupling I was avoiding is already paid there. A new project would have added infrastructure and an `InternalsVisibleTo` without removing any coupling — I was optimising against a cost that was already sunk. I'll write the test in `tests/StyloMail.Transport.Tests/` and own the assertions.

## 2. A real bug in the harness, found by `mime-`, and it was mine

**Parameterised `[Theory]` failures were invisible to name extraction, producing false `ELSEWHERE` verdicts.**

The `[FAIL]` regex required non-space characters immediately before `[FAIL]`, but a theory's failure line carries its parameters in between — and those contain spaces, commas and escaped newlines. So the regex matched *nothing*: `failed` came back empty while the summary count was correct, and my branch order fell through `count == 0 → GAP`, then `claims in failed → CLAIMED`, to **ELSEWHERE**. The claiming test had gone red exactly as claimed, and the harness reported "investigate — redundant guard or untested claim."

Fixed by parsing the TRX XML as `mime-` specified. Their lane: **17 CLAIMED + 2 false ELSEWHERE → 19 CLEAN.** I added a cross-check they didn't ask for: the console summary and the TRX must agree, or the verdict is INCONCLUSIVE. The reason this went unnoticed is that the two readings *looked* consistent while actually contradicting each other — so a future parsing gap now surfaces loudly instead of as a confident wrong answer. I also added multi-edit support (`old`/`new` as lists) so a mutation needing two changes is expressible rather than omitted.

**The part worth your attention:** `ELSEWHERE` is the verdict that tells a lane "investigate", so false positives would have trained us to ignore the one signal worth acting on — and a lane whose claim is a theory could *never* record CLAIMED. **My own lane has no `[Theory]` tests, so my 24 CLAIMED were unaffected by luck, not design.** I nearly wrote a theory for the thread-safety tripwire and changed it to a `[Fact]` for unrelated reasons. That's the same shape as the rest of today, one level up: I built a mechanism to catch a class of error, and it was subject to that class.

## 3. Solution state

`dotnet build StyloMail.slnx` is red on **`StyloMail.Host`** — `HostIngressSink.cs` / `HostTransportOptions.cs` can't resolve the `StyloMail.Transport` namespace, so `host-` is mid-wiring of the Transport reference (gap 1/3 work). Not mine, not chasing. My projects: `Queue` builds, 88 green, 24/24 CLAIMED.

**Applying your own rule to myself**, which is why I checked: I'd just edited `StyloMail.slnx`, and a red solution is exactly what that would look like if I'd broken it.

**Proceeding with (b).** I'll need `transport-` to confirm I can add a file to their test project, and I'll assert against their now-settled cancellation tests rather than re-deriving the contract.
