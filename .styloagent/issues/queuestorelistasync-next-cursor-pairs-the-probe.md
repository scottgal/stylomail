**From:** ingress-
**Timestamp:** 2026-09-22T14:45:52.6778130+01:00
**Severity:** high
**Status:** open
**Source:** internal

# QueueStore.ListAsync next-cursor pairs the probe row's timestamp with the kept row's id, so paged listings silently skip items

Found by `ingress-` while wiring `GET /v1/messages` onto `QueueStore.ListAsync`. Owned by `queue-` (src/StyloMail.Queue/QueueStore.Listing.cs).

DEFECT
`ListAsync` fetches `limit + 1` rows so the extra one tells it whether a further page exists. It tracks `lastCreatedAt` by overwriting it on every row the reader yields, so after the loop that variable holds the **probe** row's `created_at`. The cursor is then built as `EncodeCursor(FromDb(lastCreatedAt), ids[^1])` — the probe row's timestamp paired with the id of the last row **kept**, which is a different row, and the probe row has just been removed from `ids`.

The next page queries `created_at < $afterAt OR (created_at = $afterAt AND queue_id < $afterId)`. Rows are ordered `created_at DESC`, so the probe row's timestamp is older than the kept row's: every row whose `created_at` falls between the two is skipped, and the probe row itself survives only if its GUID sorts below the kept row's.

IMPACT
A paged listing returns fewer items than exist and reports `hasMore: false`. Nothing errors. The operator console's message list would show two of three messages with no indication anything was omitted, and `GET /v1/messages` is now the surface that exposes it.

INTERMITTENT BY NATURE — which is the risk to the triage process
The skip depends on a GUID comparison, so it fires on roughly half of runs rather than reliably. It presents as a flaky test, and three false flake attributions have already cost this fleet an afternoon. The failing test is `StyloMail.Host.Tests.ListingEndpointsTests.The_message_listing_pages_with_the_queues_own_cursor`, and the mechanism is written into its comments so it is not "fixed" by loosening the assertion.

EVIDENCE
6 failures in 12 consecutive runs of that single test. Three messages submitted, page size 2: page one returns two items and a cursor; page two returns zero with `hasMore: false`.

REQUESTED
Fix in `QueueStore.ListAsync` — both halves of the cursor must come from the last kept row — and a test in the queue's suite that pages a set larger than the page size and asserts the union of pages equals the set. Reported to `queue-` directly (urgent) and flagged to `overview-` and `desktop-`.
