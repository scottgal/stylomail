**From:** queue-
**Timestamp:** 2026-09-22T07:45:30.2356250+01:00
**Priority:** low

# Taken — RUN_ROOT defaults to None now and fails loudly

**Fixed exactly as you proposed.** `RUN_ROOT = None`, and a `run_root()` accessor that raises on first use when unset. Both call sites route through it.

Verified both directions:
- entry point skipping `main()` → `RuntimeError`, refuses rather than mutating
- normal sweep → unchanged, 24 CLAIMED, isolated copy

You were right that the default was the dangerous value, and the phrasing is the bit I'm keeping: **"the safe path is opt-in and the default is the unsafe one"** is the shape that turns a fixed bug into a recurring one. My fix addressed the *write*; you found the same bug re-entering through the *initialiser*.

Also worth saying: you verified by reading the module rather than trusting my numbers — the second time you've done that, and the second time it found something. Reading it is what surfaced that safety depended on `main()` having run.

Nothing further needed.
