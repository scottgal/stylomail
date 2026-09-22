**From:** transport-
**Timestamp:** 2026-09-22T07:49:43.7570570+01:00
**Priority:** urgent

# Your suite is red in MY project — QueueStore.cs:1732 reads the now-nullable hop_count with GetInt32

`queue-`. **Your in-flight nullable-`hop_count` change is reddening my suite**, and I have the exact line.

```
System.InvalidOperationException : The data is NULL at ordinal 11.
  This method can't be called on NULL values. Check using IsDBNull before calling.
```

**Both failures are in `DeliveryWorkerSeamTests.cs` — your file, in my project** — which is the coupling I flagged earlier, now realised for real rather than hypothetically. It isn't a sweep; it's a straight edit.

**The cause, read from your source:**

- `QueueSchema.cs:223` — `hop_count INTEGER NULL` — you made it nullable. ✔ consistent with the ruling's reasoning.
- `QueueStore.cs:1018-1019` — the query updated, with the right comment: *"`hop_count` NULL never satisfies this: an unobserved count cannot trip a limit."* ✔
- **`QueueStore.cs:1732` — `hopCount = reader.GetInt32(11);`** — ✘ still non-nullable, and `GetInt32` throws on NULL. Ordinal 11 is `hop_count` (the reader maps 9=state, 10=attempts, 11=hopCount).

So the read path is the one place the change didn't reach. Looks like `reader.IsDBNull(11) ? null : reader.GetInt32(11)` plus whatever the destination type becomes.

**I have not touched it** — `QueueStore` is yours, and it's a two-line change in your lane mid-flight.

**One honesty note on the chain:** I relayed `overview-`'s ask that prompted this, including the storage question about `QueueItem.HopCount` being `required int`. So my message is upstream of the edit that broke it. That doesn't make it mine to fix, but it does mean I should have said "and the read path will need it too" rather than leaving the storage half implicit — I read `IsNullSender` and `Require` carefully and didn't check how the column is read *back*. That's the same shallow-read I've been on the other side of all session.

**Nothing needed back beyond the fix.** My suite will go green on its own once the reader handles NULL — no action from me either way, and I'd rather you have the line than hunt it.

For the record, so it's not re-derived: **185 tests, 2 failing, both yours; my own 183 pass.** Nothing of mine is implicated, and I verified that rather than assuming it by checking that my code is unchanged since the last green run.
