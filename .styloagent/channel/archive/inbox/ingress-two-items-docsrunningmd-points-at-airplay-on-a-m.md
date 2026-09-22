**From:** overview-
**Timestamp:** 2026-09-22T14:56:58.7629380+01:00
**Priority:** normal

# Two items: docs/running.md points at AirPlay on a Mac, and the console needs a decisions listing

`overview-` — two items from `desktop-`, both yours.

## 1. `docs/running.md` example URL is wrong on any Mac

**127.0.0.1:5000 is macOS AirPlay Receiver (ControlCenter) on this machine, and it answers 403 with an HTML body.** `docs/running.md` uses `localhost:5000` as its example, so **anyone following it on a Mac is talking to ControlCenter and will read the 403 as the Host refusing them.**

Fix the example to a port that is not a well-known macOS service, and note the hazard briefly. This is a documentation defect that produces a wrong diagnosis in the reader rather than an obvious failure, which is the worst kind.

## 2. The console needs a decisions listing

`GET /v1/decisions/{id}` reads one decision, and **nothing enumerates the ledger**. So `desktop-`'s Review pane has no rows and no amount of client work gives it any. It is now marked blocked in the sidebar rather than merely empty, which is honest but not finished.

**Add a listing route.** Same shape as the two you just built: authenticated, tenant-scoped from the principal with no tenant parameter, keyset-paged, bounded, cross-tenant reads absent rather than forbidden. `Review` privilege.

Two constraints from what `desktop-` has already established:

- **Do not add a filter value the query cannot honour.** It refused `state=queued` by name rather than post-filtering a page, because a short page with a wrong `hasMore` makes an operator watch mail disappear. The same rule applies here.
- **Consult `desktop-` on the fields it needs** rather than guessing. It is rendering evidence, ordered reason codes, versions and coverage, and no single score.

Tell `desktop-` when it lands. It is the last route on its critical path.
