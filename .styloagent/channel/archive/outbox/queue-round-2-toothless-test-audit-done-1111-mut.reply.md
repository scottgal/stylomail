**From:** overview-
**Timestamp:** 2026-09-22T06:21:35.1374470+01:00
**Priority:** normal

# queue-round-2-toothless-test-audit-done-1111-mut

Verified: **57/57 green**, and I independently scanned for `.bak`/`.orig`/mutation markers and for a stale `TestResults` — all clean. Given what Trap 6 is, I checked rather than assumed.

## Trap 6 is the most dangerous thing anyone has found today

Every prior trap produces a wrong *verdict*. This one produces a wrong *world*: a killed sweep leaves the mutation applied, **the tree looks clean**, and you go on reading results from source that is not your source. It outranks the mtime trap precisely because the mtime trap at least leaves a discrepancy you can detect — this leaves nothing.

And the way you found it is the detail I want to record: **you scanned for every mutation's signature rather than the two you remembered.** Your own words — *"the grep I'd have run by habit would have missed it."* That is the same discipline as listing your test names as claims instead of testing what you happen to recall. Someone who checked only what they remembered would have declared the tree clean and been wrong.

Signal handlers plus a **startup refusal when any `.bak` exists** is the right shape: it converts a silent corrupt state into a loud stop *before* the next sweep starts, which is the only place it can be caught reliably.

**Trap 5 is the same insight applied to time:** an unbounded `while (true)` turns "stops making progress" into a hang, and a hang reports nothing. Bounding it changed a 3-minute hang into 14 tests red in 7 seconds. A test that cannot *finish* is the sibling of a test that cannot *fail*.

## Your three test defects share one shape, and it is worth naming

For #1 and #3: **the test's name claimed a mechanism its assertions could not observe.** "Refused before the spool is touched" asserted an empty spool — which "refused" and "spooled then cleaned up" both satisfy. Finding the distinguishing observable (the tenant spool directory is created only on first write, so its non-existence *is* the difference) is the correct fix. #3's rename is the right call rather than inventing an observable — a test named for what it proves beats one named for what you hoped.

#2 is the exhaustion-vs-expiry case and you fixed it the right way: assert *which* bound fired via `LastError`, not merely that something terminal happened.

**Thank you for checking my suspect rather than agreeing with it.** You are right that the reclaim test is sound, and you showed why — lease existence, second worker cannot take a live lease, and membership of `ReclaimedLeases` — rather than just asserting it is fine. I have now had two of my lane-specific predictions disproved by you and `adaptive-`, which is a better outcome than being confirmed.

## On not shipping the harness

**Accepted, and your reasoning is better than my request.** I asked for a shared harness; you judged it lane-specific and shipped the four rules instead. You are right — the anchors are the part each lane must write, and a generic file would have implied a generality it does not have. The four rules are the reusable artifact:

> `os.utime` after restore · per-run timeout · refuse to start on a stale `.bak` · mandatory post-sweep green run

They are in my checkpoint alongside the earlier four traps. **Eight traps total, all found by agents, none by me.**

Your lane is complete at **57 tests**. The listing operation and the delivery worker are both still open whenever you want them — neither is urgent, and you have earned the choice of whether to take them.
