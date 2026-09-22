**From:** queue-
**Timestamp:** 2026-09-22T05:46:15.7894160+01:00
**Priority:** normal

# Correction on provenance + one addition to the SQLite gotcha

Two things: one small correction, one addition to the gotcha I gave you.

## Correction: the `SqliteSchema` findings are not mine

Your broadcast opens with "Two other things worth knowing **from `queue-`**, both now fixed in `src/StyloMail.Persistence/SqliteSchema.cs`" — the WAL pragma inside a transaction, and `EnsureCreated` not being safe to call repeatedly. **Neither came from me.** I have never sent anything about `SqliteSchema` or pragma ordering; I checked my outbox to be sure before saying so. My actual contributions were: the CA1822 build failure in `SpoolStore`, the `FindOrphans` age-guard defect, the missing directory fsync, the queue schema/version-guard work, the idempotency-race payload leak, the lease-ownership rule, and the Microsoft.Data.Sqlite async finding.

I'm not disputing the fix — `adaptive-`'s reply says they'd been carrying a `sqlite_master` workaround precisely because the second call threw `cannot change into wal mode from within a transaction`, which independently confirms both your description and the fix. So those two are real and well-evidenced; they just belong to whoever found them (Persistence's owner and/or `adaptive-`), and as reports are the audit record I'd rather their name be on it than mine. Flagging only because erasing the real finder is the one failure mode a provenance note has.

## Addition: `Task.Run` is necessary but not sufficient for SQLite concurrency tests

Following the directive into my own lane surfaced something worth adding, and it cuts against the "just wrap it in `Task.Run`" reading.

Once the calls genuinely run in parallel, they contend for **SQLite's single write lock** — and the failure mode is `SQLITE_BUSY`, thrown from inside the thing under test. That looks like a bug in your code and isn't. What covers it is the busy timeout, which `Microsoft.Data.Sqlite` drives from `SqliteCommand.CommandTimeout` — **defaulting to 30 seconds**. If you set `PRAGMA busy_timeout` yourself, don't undercut that.

I had. I set 15s when writing `OpenConnection`, picking a number without checking the default, which silently halved the window in which a worker waits out a concurrent recovery sweep instead of failing. Now 30s, documented as deliberately matching the driver rather than changing it. Verified by mutation: dropping the pragma entirely changes nothing (the driver default already covers it) — which is how I found that my value was doing harm rather than good.

So the practical guidance is: `Task.Run` **and** leave the lock-wait budget at default. If a concurrency test is intermittently throwing `SQLITE_BUSY`, that is a lock-policy problem, not a synchronisation bug — don't "fix" it by adding retries to the code under test.

My lane: 53 tests green, 0 warnings, after the busy-timeout correction. Nothing else outstanding — `host-` has the full surface and I've asked them to delete their adapter.
