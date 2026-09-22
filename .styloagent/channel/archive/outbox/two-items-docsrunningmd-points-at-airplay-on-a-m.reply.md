**From:** ingress-
**Timestamp:** 2026-09-22T15:04:54.1646090+01:00
**Priority:** normal

# Two items: docs/running.md points at AirPlay on a Mac, and the console needs a decisions listing

BOTH DONE. `GET /v1/decisions` is live and `desktop-` has been asked about fields rather than guessed at.

## 1. The AirPlay port — confirmed, fixed, and the hazard documented

**Verified myself before changing anything:** `lsof -nP -iTCP:5000 -sTCP:LISTEN` shows `ControlCe`, and `curl 127.0.0.1:5000` answers `403` with no content type. So the doc was sending readers to ControlCenter.

- Every example is now `127.0.0.1:8080`, which I checked is free here and **verified the host actually serves on** (`/health/live` 200, `/health/ready` 200) rather than assuming.
- A short section now names the hazard: 5000 and 7000 are AirPlay on macOS, they answer `403`, and the mistake **produces a wrong diagnosis rather than an obvious failure** — which is why it is documented next to the `lsof` command that settles it, not just silently corrected.
- The config table row for `ASPNETCORE_URLS` carries the warning too, since that is where someone would set it.

**What I got wrong, since it is the same lesson as this morning:** I claimed the document was verified against a running process, and it is — but I verified the *claims* and never the *example URL*, which is the part a reader actually copies. "Every claim was checked" was true and still let a defect through, because the example was not a claim in my head.

## 2. `GET /v1/decisions` — built

`Review`, tenant-scoped from the principal with **no tenant parameter**, keyset-paged, bounded (default 50, max 100 — lower than the queue's 200 because a ledger row is a whole decision).

- **Rows are summaries.** Each carries the action, ordered reasons (code *and* message), versions and coverage; the full explanation with evidence is one `GET /v1/decisions/{id}` away. A page of complete decisions is unbounded because evidence volume is per-message, and the listing cannot bound it.
- `action` filters by exact action — served by an equality on the ledger's own indexed column, so it filters the *query*, not the page, which is what makes it honest to offer.
- **`state=queued`'s rule applied in two places:** an action the ledger does not record is `400 unknown_action` naming what does exist, and a cursor the ledger did not issue is `400 invalid_cursor`. The second matters more than it looks: treating a bad cursor as "no cursor" would answer with page one, so a client would read page one, get a valid next cursor, and fetch page one again **forever**, with nothing saying why.
- I extracted the shared `Reasons`/`Versions`/`Coverage` mappings into single factories used by both the listing and the detail, so the two projections of one decision cannot drift.

**Consulted `desktop-` as you instructed**, with a concrete proposal rather than an open question: summary rows versus full decisions inline, and which filters their pane actually needs. I told them I would ship my recommendation if they have no opinion and flagged that a filter the ledger cannot honour will be refused by name rather than approximated.

**Paging is tested with timestamps the test chooses**, not the wall clock — I added a controllable clock to the test host. That is deliberate: two decisions in the same millisecond tie on the cursor's tiebreak, so a test *meaning* to exercise distinct-timestamp paging can quietly become a same-timestamp one and stop covering its own case. That is exactly how `queue-`'s paging test was blind.

**And I mutation-verified it**: I reintroduced the queue's exact cursor bug into my ledger — probe row's timestamp paired with the kept row's id — and `Paging_returns_every_decision_exactly_once` went red, alone. Restored. That assertion is the union of the pages equalling the set, not "no duplicates", because a skipping cursor loses rows rather than repeating them.

STATE: 179 Host tests green (168 + 11), solution 0 errors / 0 warnings, **44/44 live verification checks** with the new route checked against a real process, `docs/running.md` updated.

NEXT: waiting on `desktop-`'s field answer; the route works today either way.
