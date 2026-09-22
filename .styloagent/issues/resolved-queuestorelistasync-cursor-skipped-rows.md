**From:** ingress-
**Timestamp:** 2026-09-22T14:47:34.8747440+01:00
**Severity:** low
**Status:** closed
**Source:** internal

# RESOLVED — QueueStore.ListAsync cursor skipped rows across pages (fixed by queue-, verified 6/12 red to 0/15)

Resolution record for the issue filed earlier the same hour. Filed by `ingress-`; fixed by `queue-` the same afternoon.

DEFECT (as filed)
`QueueStore.ListAsync` fetched `limit + 1` rows and tracked the probe row's `created_at` while pairing the cursor's timestamp with the last *kept* row's id, so the following page skipped every row whose timestamp fell between the two. Silent — fewer rows returned with `hasMore: false` — and intermittent, since the surviving probe row depended on a GUID tiebreak.

FIX
`queue-` changed the loop to collect `(QueueId, CreatedAt)` rows and built the cursor from `rows[^1].CreatedAt` with `rows[^1].QueueId`, both from the same row.

VERIFICATION (ingress-)
Same measurement that found it: `StyloMail.Host.Tests.ListingEndpointsTests.The_message_listing_pages_with_the_queues_own_cursor` was **6 failures in 12 runs** before, and **0 in 15** after. `dotnet build StyloMail.slnx` 0 errors / 0 warnings; 168 Host tests green.

NOTES WORTH KEEPING
1. The assertion with teeth is that the **union of pages equals the submitted set** — a "no row appears twice" assertion passes against this defect, because it loses rows rather than duplicating them. It also needs a page size smaller than the set, since anything else never builds a cursor.
2. The test's comment originally read "KNOWN RED, intermittently". That was true while the bug was live and became a false claim about the code the moment it was fixed. `ingress-` rewrote it in the same change; stale records about fixed defects are the same failure class as the stale `MailFrom` doc comments.

No further action. Closed against the fix.
