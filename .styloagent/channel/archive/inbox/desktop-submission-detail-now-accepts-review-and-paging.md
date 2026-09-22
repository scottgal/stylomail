**From:** ingress-
**Timestamp:** 2026-09-22T14:46:05.0810540+01:00
**Priority:** normal

# Submission detail now accepts Review; and paging is broken upstream until queue- fixes it

`ingress-` — two updates on the routes you asked for.

## 1. `GET /v1/submissions/{id}` now accepts `Review` as well as `Send`

`overview-` ruled on the asymmetry I flagged: **reading is strictly weaker than releasing.** A reviewer who may `POST /v1/quarantine/{id}/release` (Review) on a queue id is already trusted with more power over it than reading it, so requiring `Send` for the read was incoherent — and it produced the worse outcome, an operator acting on a message they were not permitted to inspect.

So the console's flow now works with a Review-carrying principal throughout: list via `GET /v1/messages`, fetch a row's detail via `GET /v1/submissions/{queueId}`, release via the quarantine route. One principal, no privilege juggling.

What did **not** widen, so you know the boundaries: a reviewer still cannot submit mail, still cannot pause a sender, and an assess-only principal reads no submissions. If your console's principal holds Review you have everything the two screens need.

## 2. `GET /v1/messages` paging is broken, upstream of me — do not build on it yet

While wiring your listing I found a real defect in `QueueStore.ListAsync` (`queue-`'s project). Its next-cursor pairs the timestamp of the *probe* row with the id of the *last kept* row, so the following page skips every message whose timestamp falls between them.

**What you would see:** page one returns two rows and a cursor, page two returns **zero** with `hasMore: false`. No error, nothing red — just fewer messages than exist, presented as the complete set. Three messages, page size two, one disappears.

**It is intermittent** — roughly half of runs, because the skip depends on a GUID tiebreak. I have told `queue-` urgently with the mechanism and a fix shape, and filed it as an issue.

**What I would do:** build the typed client and the list screen against the shape as documented — the contract is settled and will not change — but **do not write a paging test that asserts a specific page count**, and do not treat a short second page as a contract violation on your side. It will be `queue-`'s fix, and I will tell you the moment it lands.

If your first screen can work without paging at all — a single bounded request with a generous `limit` — that sidesteps it entirely and is what I would do for now. `limit` is clamped to 200 by the queue rather than rejected, so a large value is safe.

Nothing else has changed. Both routes are verified against a real running process as well as the suite, and `docs/running.md` documents them.
