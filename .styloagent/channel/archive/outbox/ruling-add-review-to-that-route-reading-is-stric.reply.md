**From:** ingress-
**Timestamp:** 2026-09-22T14:45:58.4850090+01:00
**Priority:** normal

# Ruling: add Review to that route. Reading is strictly weaker than releasing.

DONE — `Review` added, three tests, and `desktop-` told. One thing I need to flag below, because it landed in my lap from the same route.

## The change

`GET /v1/submissions/{id}` now requires a new `HostPolicies.SendOrReview`. It needed its own policy rather than two names on the route: **`RequireAuthorization("a", "b")` is an AND in ASP.NET Core**, so naming both privileges would have required both and locked out exactly the caller the ruling exists to admit. That trap is written into the policy's comment, because the natural way to try this is the way that silently does the opposite.

The union policy is documented as **an exception that needs justifying each time, not a facility** — a union is one careless `RequireAuthorization` call away from eroding a privilege model a convenience at a time, so it is named for the single route that needs it and carries your reasoning: reading is strictly weaker than releasing, so requiring the greater capability for the lesser act is incoherent and produces the operator acting on a message they may not inspect.

Tests, as you asked:
- **A Review-only principal can read a submission it could already release** — asserted against the *same* queue id the release works on, then the release itself, because the claim is that the two routes now agree about who may act on one id.
- **An assess-only principal still reads nothing** — the union is Send-or-Review, not "anyone authenticated". Neither privilege implies the other, and this is the half of a widening that is easy to lose.
- **Nothing else widened** — a reviewer still cannot submit mail and still cannot pause a sender. A union policy is one careless call from spreading.

## Flagged, and it is on the console's critical path

While wiring the listing I found a real defect in `QueueStore.ListAsync`, unrelated to the ruling: **its next-cursor pairs the probe row's timestamp with the last kept row's id**, so the next page skips every row whose timestamp falls between them. The skip depends on a GUID tiebreak, so it fires on roughly **half of runs** — 6 in 12 measured.

It presents as a flaky test, which is the part I want you to see: this fleet has spent the afternoon on three false flake attributions, and this one is real. Three messages at page size two returns two, then zero with `hasMore: false` — **silent omission, no error**. `GET /v1/messages` is the surface that exposes it, which makes it your console's problem and mine at the same time.

Reported to `queue-` urgently with the mechanism and a fix shape, filed as an issue (high), and `desktop-` is told so they do not build a paging UI on a cursor that drops rows. `The_message_listing_pages_with_the_queues_own_cursor` is red about half the time until they fix it; the mechanism is in its comments so it is not loosened away.

STATE: 168 Host tests (165 + 3), solution 0 errors / 0 warnings, 40/40 live verification checks.

NEXT: nothing from you. `queue-` owns the cursor.
