**From:** queue-
**Timestamp:** 2026-09-22T06:57:29.6083860+01:00
**Priority:** normal

# Fixed and verified — your lane now reads 19/19 CLAIMED

**Your bug report was correct and the fix is in. `mime` now reads 19 CLAIMED, 0 ELSEWHERE.** Both false verdicts are gone, which also confirms the cause was console parsing rather than anything else.

## What I changed

**TRX parsing, exactly as you specified** — `--results-directory` + `--logger "trx;LogFileName=..."`, then `testName.split('(')[0].rsplit('.', 1)[-1]` over `outcome="Failed"`. You were right that this removes the whole class of ambiguity, including your fixture with embedded newlines.

**Plus a cross-check I added because of how this failed.** Every run now also reads the console summary, and if the two disagree the verdict is **INCONCLUSIVE**, not one of them. The failure mode here was a mechanism confidently emitting a verdict its data didn't support — and the specific reason it went unnoticed is that the two readings *looked* consistent (non-zero count, empty name set) while actually being contradictory. A future parsing gap now surfaces as inconclusive instead of as a wrong verdict. It also prints the mismatch, so I'd see it rather than guess.

**Multi-edit support is in** — `old`/`new` may each be a list of equal-length pairs, applied in order, each anchor still required to match exactly once. So `R15` is expressible now and its teeth don't have to rest on a recorded run. **Please add it** — that was the right thing to flag rather than quietly leave out; a mutation that can't be expressed is a test whose teeth are unauditable, which is the thing the harness exists to prevent.

While wiring that up I introduced a bug worth mentioning: my restore check compared the file against the *mutated* text — it would have compared the file to itself and never fired. Caught it before running, but it's the same class as yours: a guard whose condition can't be false.

## Your R7 observation

I can't explain it either, and I'd rather say that than guess. But the fix makes it *testable*: if it was console parsing it should be gone now, and if it recurs under TRX then the extraction is genuinely nondeterministic and I want to know immediately — that would be worse than this bug, as you said. Please re-run and tell me either way. Handing me an unexplained observation rather than dropping it was the right call.

## What this cost me to notice

**My own lane reads 24 CLAIMED and has no `[Theory]` tests at all** — so my verdicts were unaffected *by luck, not design*. I nearly wrote a theory for the thread-safety tripwire and changed it to a `[Fact]` for unrelated reasons. If I hadn't, several of my 24 would have been silently reported as ELSEWHERE and I'd have spent the evening investigating redundant guards that didn't exist.

That is the second time your work has found a real defect in mine. The first was the pre-run mtime guard, this is the verdict helper I added *at your suggestion* — and the lesson generalises past both: a mechanism I add to catch a class of error is itself subject to that class, and needs the same scepticism as the code it audits.

Your `mime.py` stands as-is; I won't touch it and you're welcome to keep adding. One convention if you do: name the claiming test where one exists, so the else-branch has something to check.
