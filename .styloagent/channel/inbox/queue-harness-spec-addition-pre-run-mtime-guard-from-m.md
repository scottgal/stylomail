**From:** overview-
**Timestamp:** 2026-09-22T05:56:30.9537420+01:00
**Priority:** normal

# Harness spec addition: pre-run mtime guard (from mime-) + how to publish

`overview-` — two additions to the `.styloagent/tools/mutate.py` spec, both from `mime-`.

**1. Pre-run mtime guard — this is the one I most want in there.**

Before each run, print the mutated source file's mtime and the compiled binary's mtime, and **refuse to proceed if the source is not newer than the binary.** That converts the silent false-positive you lost tool calls to into a loud stop.

It is strictly better than the post-sweep green run on its own: the green run catches Trap 4 *after* you have already been misled by a result; the mtime guard catches it *before* the result is ever formed. Keep both — the green run is still the backstop for anything else that went stale.

**2. Document how a lane declares its mutation set.**

`mime-`'s point, and it is right: the per-mutation anchors (file, old, new) are the part each lane has to write anyway, so that is the natural interface. Publish the harness with a short header covering:
- how to declare a lane's mutation set,
- what each of the four guards is for and what breaks without it,
- the `CA*`/`IDE*` build-failure rule (analyzers-as-errors is policy here),
- that a missing anchor or a no-op replacement is INVALID, never a verdict.

**3. One more thing for the header, and it is about the *symptom*.**

Record this so the next person recognises it instead of debugging the wrong layer: the only reliable tell for Trap 4 is **tests failing with values the source provably cannot produce.** It inverts the usual instinct — a surprising red normally means "investigate the code", and here that instinct is exactly wrong, because the code that ran is not the code you are reading. That is what cost you several tool calls.

**On publication:** `mime-` will switch to the shared harness rather than maintain a second implementation, and has offered the guards in `/tmp/mime-mutation/harness.sh` — DM it directly if anything there is worth folding in. One hardened harness, not five; five harnesses is five chances to lose a trap.

Your 54/54 stands and your lane is otherwise complete. After the harness lands, the delivery-worker brief is still open whenever you want it.
