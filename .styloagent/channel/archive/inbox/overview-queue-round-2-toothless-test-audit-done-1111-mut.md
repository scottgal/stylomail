**From:** queue-
**Timestamp:** 2026-09-22T06:20:44.6850400+01:00
**Priority:** normal

# queue- round 2: toothless-test audit done, 11/11 mutations caught, + 2 harness traps

Ran both asks (round-2 toothless tests, and the shared-state generalisation). **57 green.** Found three real defects — two in my tests, one in my harness. Nothing in my source.

## The cheap method worked — three real finds, in under a minute each

Listing test names as claims and asking "what else produces this?" found two immediately:

**1. `Payloads_over_the_size_limit_are_refused_before_the_spool_is_touched` couldn't see its own claim.** It asserted an empty spool file list — which is satisfied *both* by "refused before spooling" and by "spooled then cleaned up". A mutation moving the size check after the write kept it green. Fixed by asserting the distinguishing observable: the tenant's spool directory is created only on first write, so its **non-existence** is the difference. Mutation J (size check removed) now goes red.

**2. `Retries_are_bounded_and_the_recipient_ends_in_terminal_failure` asserted only "terminal"** — which expiry, hop limit and permanent failure all produce. This is exactly the exhaustion-vs-expiry case you flagged. Fixed by asserting *which* bound fired via `LastError`. Mutation K now goes red.

**3.** `A_refusal_after_spooling_does_not_leave_the_payload_behind` — same shape as #1; the name claimed a mechanism the assertions can't observe. Renamed to what it proves, with a comment naming the test that does pin the post-spool delete (the idempotency-race test, the only path where a writer definitely spools before being refused).

**Your specific suspect — "is the reclaim test green because reclaim worked, or because the item was never leased?"** It's sound: it asserts the lease exists, that a *second* worker cannot take a live lease, and membership of `ReclaimedLeases` (a list only that pass populates). Confirmed by mutation I, which it catches.

## Shared-state generalisation — applied

`SpoolStore` is stateless (one readonly field). `QueueStore` has exactly one mutable field, `_initialised`, a write-once lazy under a semaphore. Both shareable, now **asserted**:
- a reflection tripwire with an explicit allow-list, so a *new* mutable field fails loudly with a message telling the next person to either justify it or stop sharing the instance;
- a concurrency equivalence test — 24 messages drained by 1 worker vs 8, asserting the **same end-state distribution**, plus "each message delivered exactly once" read straight from the attempt history. Contention breaking the lease would show there as extra Delivered rows, before it showed as a duplicate in an inbox.

## Two more harness traps (both mine)

**Trap 5 — a mutation harness must bound every run.** Mutation I made me discover this the hard way: it made every completion a no-op, so my new drain loop spun forever. But the real defect was **my test's unbounded `while (true)`** — "stops making progress" became a *hang*, which blocks the entire run and reports nothing. Now bounded with a message naming the likely cause. Mutation I went from a 3-minute hang to 14 tests red in 7 seconds.

**Trap 6 — my harness was not crash-safe, and a kill corrupts the tree.** I killed a hung sweep; it had applied a mutation and the kill landed mid-iteration, leaving mutated source on disk. Worse than the mtime trap: the tree *looks* clean. Now: signal handlers that restore on SIGINT/SIGTERM, and a **startup refusal if any `.bak` exists**. I also caught it because I scanned for every mutation's signature rather than the two I remembered — the grep I'd have run by habit would have missed it.

Full sweep: **11 mutations, 11 caught, post-sweep tree verified green, 36s.** Artifacts from `--blame-hang-timeout` cleaned up (`TestResults/` is gitignored, but I removed them anyway).

I'm not shipping my harness file — it's lane-specific. The four rules are the reusable part, and they're now in my saved context: `os.utime` after restore, per-run timeout, refuse to start on a stale `.bak`, mandatory post-sweep green run.
