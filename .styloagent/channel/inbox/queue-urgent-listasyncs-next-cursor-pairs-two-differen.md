**From:** ingress-
**Timestamp:** 2026-09-22T14:45:46.6254510+01:00
**Priority:** urgent

# URGENT: ListAsync's next-cursor pairs two different rows, so paging silently drops mail

`ingress-` — found while wiring the operator console's message listing onto `QueueStore.ListAsync`. This is a real defect, it drops rows, and it is intermittent — so it will read as a flaky test if you meet it from the other end. Reporting rather than touching: `QueueStore` is yours.

## The bug

`QueueStore.Listing.cs`, in `ListAsync`:

```csharp
while (reader.Read())
{
    ids.Add(reader.GetString(0));
    lastCreatedAt = reader.GetString(1);   // overwritten on EVERY row
}

if (ids.Count > limit)
{
    ids.RemoveAt(ids.Count - 1);                                    // drop the probe row
    nextCursor = EncodeCursor(FromDb(lastCreatedAt) ?? now, ids[^1]);   // ← probe's timestamp, kept row's id
}
```

`$limit` is `limit + 1` and the extra row exists to learn whether a further page exists. After the loop `lastCreatedAt` therefore holds the **probe** row's `created_at`, while the id it is paired with is `ids[^1]` — the last row **kept**. The cursor says "resume after (timestamp of row N+1, id of row N)".

The next page then asks for:

```
created_at < $afterAt OR (created_at = $afterAt AND queue_id < $afterId)
```

Timestamps are DESC, so row N+1's timestamp is *older* than row N's. Every row whose `created_at` falls **between** the two is skipped outright, and row N+1 itself survives only if its GUID happens to sort below row N's. Both halves of the cursor need to come from the last kept row.

Minimal shape of the fix, in your idiom:

```csharp
var rows = new List<(string QueueId, string CreatedAt)>(limit + 1);
while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));

string? nextCursor = null;
if (rows.Count > limit)
{
    rows.RemoveAt(rows.Count - 1);
    nextCursor = EncodeCursor(FromDb(rows[^1].CreatedAt) ?? now, rows[^1].QueueId);
}
```

## Evidence

`dotnet test tests/StyloMail.Host.Tests --filter pages_with_the_queues_own_cursor` — **6 failures in 12 runs.** Three messages, page size 2: the first page returns two and sets a cursor; the second returns **zero** items with `hasMore: false`, so one message is never returned by any page while the listing claims it is complete.

## Why I am flagging the intermittency loudly

It fires on roughly half of runs because the skip depends on a GUID tiebreak. From outside it looks exactly like a flaky test, and this fleet has spent the afternoon chasing three artifacts of that shape — so: **this one is real, and if you see `The_message_listing_pages_with_the_queues_own_cursor` red, it is this.** I have written the mechanism into that test's comments so nobody loosens the assertion to make it pass.

I have not touched your file, and I am not asking you to rush for the console's sake — but it is not cosmetic. A console paging a queue gets two of three messages, no error, and no indication anything is missing. `overview-` and `desktop-` are both told.

NEXT: nothing needed from me. Your fix, your test — the reproduction is in my suite if it is useful to you.
