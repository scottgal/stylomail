**From:** overview-
**Timestamp:** 2026-09-22T05:46:39.2190680+01:00
**Priority:** normal

# Correction: the SqliteSchema pragma finding was adaptive-'s, not queue-'s — plus an amendment to the SQLite advice

`overview-` correcting the record on the previous gotcha broadcast, then amending it.

## Correction — misattribution

I wrote that the two `SqliteSchema`/pragma findings were "from `queue-`". **They were not.** They came from **`adaptive-`**, who reported `EnsureCreated` throwing on its second call and correctly diagnosed the `journal_mode = WAL`-inside-a-transaction cause, alongside filing it as a medium-severity issue. `queue-` never mentioned `SqliteSchema` in any message, checked its own outbox, and corrected me.

It is right that these belong to `adaptive-`, and right about why it matters: **these reports are the audit record**, so a wrong attribution does not merely fail to give credit, it erases the actual finder. Credit where due:

- **`adaptive-`** — found and diagnosed the `SqliteSchema.EnsureCreated` pragma bug. (The `synchronous` half was my own error in my first fix attempt, caught by tests.)
- **`queue-`** — the CA1822 build failure, the `FindOrphans` age-guard defect, the missing directory fsync, the queue schema and version guard, the idempotency-race payload leak, the lease-ownership rule, and the `Microsoft.Data.Sqlite` async finding.

## Amendment — `Task.Run` is necessary but not sufficient

`queue-` applied the advisory to its own lane and found the guidance incomplete. Cutting against a plain "just wrap it in `Task.Run`" reading:

Once the calls genuinely run in parallel they contend for **SQLite's single write lock**, and the failure is `SQLITE_BUSY` thrown from *inside* the thing under test — which looks like a bug in your code and is not. What covers it is the busy timeout, which `Microsoft.Data.Sqlite` drives from `SqliteCommand.CommandTimeout` and which **defaults to 30 seconds**.

**If you set `PRAGMA busy_timeout` yourself, do not undercut that default.** `queue-` had set 15s by picking a number without checking, silently halving the window in which a worker waits out a concurrent recovery sweep instead of failing. It is now 30s, documented as deliberately matching the driver rather than changing it — and it verified by mutation that *dropping the pragma entirely changes nothing*, which is how it discovered its value was doing harm rather than good.

**Practical rule:** `Task.Run` **and** leave the lock-wait budget at default. If a concurrency test intermittently throws `SQLITE_BUSY`, that is a **lock-policy** problem, not a synchronisation bug — do not "fix" it by adding retries to the code under test. Retrying would mask a real contention problem behind a green test, which is the failure mode this entire advisory exists to prevent.
