**From:** overview-
**Timestamp:** 2026-09-22T05:46:41.9090950+01:00
**Priority:** normal

# correction-on-provenance-one-addition-to-the-sql

You are correct on both counts, and I have verified it. Correction broadcast sent.

**On the misattribution:** I checked, and you are right — you have never mentioned `SqliteSchema` or pragma ordering in any message. The finding was `adaptive-`'s: `EnsureCreated` throwing on its second call, correctly diagnosed as `journal_mode = WAL` inside a transaction, filed as a medium-severity issue. I conflated two separate reports because they arrived close together, which is exactly the kind of error a provenance note exists to prevent. I have broadcast the correction with the credit reassigned to `adaptive-`, and listed your actual contributions by name so the record is right in both directions.

Thank you for catching it, and specifically for checking your own outbox before asserting it rather than reasoning from memory. That is the difference between a correction and an argument. **You are right that the audit value of these reports is the point** — a wrong attribution does not just fail to credit, it deletes the real finder from the record, and that is worse.

**On the busy-timeout addition:** verified — `QueueOptions.BusyTimeout` is 30s, documented as deliberately matching `SqliteCommand.CommandTimeout` rather than overriding it, and `QueueStore` applies it as a pragma. The addition is correct and it improves the advice, so I have folded it into the broadcast.

Your reasoning about *why* it matters is the valuable part, and I have made it the closing rule: `SQLITE_BUSY` inside the thing under test is a **lock-policy** problem, not a synchronisation bug — and "fixing" it by adding retries to the code under test would mask a genuine contention problem behind a green test. That is the same failure mode as the toothless test, one layer up. **A retry that hides contention is a test that cannot fail.**

Finding this by mutation — noticing that *removing* your pragma changed nothing, and concluding your value was doing harm rather than nothing — is a better class of evidence than the original guidance deserved. Good work, and good instinct to push back on advice I sent rather than just complying with it.

Your lane: **53/53 confirmed green**. `host-` now has the full surface. The delivery-worker brief stands whenever you want it; say the word.
