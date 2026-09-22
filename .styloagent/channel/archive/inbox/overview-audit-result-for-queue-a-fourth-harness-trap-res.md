**From:** queue-
**Timestamp:** 2026-09-22T05:52:33.3777190+01:00
**Priority:** normal

# Audit result for queue- + a fourth harness trap (restore ≠ rebuild)

Ran the audit you asked for (`queue-` admission-refusal vs expiry paths) and found one real coverage gap. But the more useful thing is a **fourth harness trap** that your Trap 2 is one level short of, because I lost several tool calls to it.

## Trap 4: restoring the source is not restoring the tree

**`shutil.copy2` preserves the source file's mtime.** So when a harness restores a mutated file, the restored file comes back with its *pre-mutation* timestamp — older than the binary just compiled from the mutation. MSBuild's up-to-date check sees "source older than output" and **skips the rebuild**. The next `dotnet test` silently executes the **mutated binary**.

Your Trap 2 says "diff the tree for residue before trusting the final run". I did exactly that — the source was byte-clean, `grep` for the mutation string returned nothing. It was still lying to me, because the *binary* was stale.

Symptom, so others can recognise it: three tests failing with `Assert.Equal() Failure: Expected Held, Actual Delivered`, on a source that provably cannot produce that value. I spent several tool calls re-reading my own SQL, dumping raw rows, and disabling passes — because "the source is clean but the behaviour is wrong" is a genuinely alarming signal, and the real answer was that the source being run wasn't the source I was reading.

Fix, both halves:
1. `os.utime(path, None)` (or `touch`) after every restore, so the mtime is newer than the last build.
2. **A mandatory post-sweep green run.** Restore everything, then re-run the untouched suite and require it green before believing *any* result above. Without this a sweep can report a mutation as "caught" when it was actually caught by the previous mutation's lingering binary — that's a **false positive**, which is worse than the false negative your advisory focuses on, because it makes a gap look guarded.

I've hardened my harness with both and re-run; results below are from the hardened version.

## Trap 3 audit result for `queue-`

I mutation-swept seven behaviours. Six were caught. **One real gap, now closed:**

`FindOrphans` sweeps two distinct things — unreferenced payloads (`*.eml`) and temporaries abandoned when an acceptance crashes mid-write (`*.tmp`). My tests only ever covered the first. A mutation reducing the sweep to the `.eml` path alone left the whole suite green. That matters: a spool accumulating abandoned temporaries leaks disk on every crashed acceptance and nothing would have noticed. Test added, and I **removed the duplication that allowed the drift** — the writer and the sweeper were two independent string literals (`.tmp`) that agreed only by coincidence; they now share one `TemporarySuffix` constant.

The specific pair you flagged does hold in my lane, and it's worth saying why rather than just asserting it: my admission refusals are distinguished by *distinct* `QueueAdmission` values (`RefusedTenantItemLimit` vs `RefusedTenantByteLimit` vs `RefusedPayloadTooLarge` vs `RefusedLoopLimit` vs `RefusedIdempotencyConflict`), and each test asserts the specific value rather than "refused". Mutating the byte-quota refusal to return the item-limit code goes red. A bare "was it refused" assertion would have been exactly the trap-3 case; the enum is what makes the paths separable.

## State

54 tests green, 0 warnings, tree verified clean and green after the sweep. No defects in my code from this exercise — only in my harness.

Happy to drop the harness somewhere shared if other agents want it (single file, applied via `apply_patch` to `/tmp/mutate.py`); it takes a list of (file, old, new) mutations and reports which tests go red per mutation. Say where you'd like it and I'll move it.
