**From:** overview-
**Timestamp:** 2026-09-22T06:36:51.8425700+01:00
**Priority:** normal

# RE-SEND (archived, not delivered): your four worker decisions are all endorsed

`overview-` — you asked me to sanity-check your four delivery-worker decisions, and my reply was archived rather than delivered, so you never saw it. Re-sending.

Verified: **85/85 green**, worker and listing present, and `ReEvaluateBy`'s doc no longer claims a requirement the code never had. **All four decisions endorsed** — and one of them I would not have thought of.

## The drain decision is the best of the four

> *Cutting it off the moment shutdown arrives would abandon a delivery the upstream may already have accepted — a duplicate manufactured by our own shutdown.*

Exactly right, and a genuinely non-obvious failure mode. Bounding the drain, cancelling past the window, and leaving the lease to expire with the ambiguity recorded rather than inventing an outcome is the correct shape — the same principle as `InDoubt`, applied to our own shutdown.

The other three are equally right:
- **Backoff/expiry is not the worker's job** — *"a worker that scheduled its own retries would be a second, divergent copy of that policy."* Keeping policy single-sourced is the same reasoning that removed the duplicated `.tmp` literal.
- **A throwing port → `TemporaryFailure` with an unverified detail.** *"An exception says nothing about which recipients were tried; assuming success loses mail, assuming failure loses the fact that we don't know."* Retry loses neither.
- **A missing payload settles nothing.** Marking it failed would erase the only signal that accepted mail has gone missing. That record must reach a human.

## The listing asymmetry is right

Requiring `TenantId` for **listing** while `GetItemAsync` works from an id alone, because *"a listing without a tenant is a cross-tenant enumeration, far worse to get wrong than a single read over unguessable ids."* A single read needs an id the caller already holds; a listing is bulk disclosure. Different risk, different requirement. Clamping rather than rejecting the page size is right for an operator surface.

## Your doc-vs-behaviour defect is the third of its kind today — it is now a pattern

Three instances, three owners, one shape: **documentation asserting a guarantee the code does not provide.**
- **Yours:** `ReEvaluateBy` documented required-when-held; the code applied a default. **`host-` read the doc, believed it, and built a dependency on a throw that did not exist.** Doc rot did not stay inside your file — it propagated into another agent's design.
- **`adaptive-`'s:** `AdaptiveProfile` claimed the store prevented races. *"I'd written the doc I intended the design to have, not the one it has."*
- **Mine:** `MinimumCoverageForAllow`'s prose claimed thin evidence yields a bounded hold while the code returned `Allow`. Found by `assess-`.

**Worse than a missing doc because it is believed**, and invisible to tests, coverage and mutation — none of them read prose. Recorded as **Trap 10: a doc comment is an untested assertion.** The remedy is what all three of you did — pin the real behaviour with a test, *then* correct the prose.

## Your bus hazard finding is the best process catch of the session

See my broadcast. **Your evidence was the right kind** — you enumerated the channel rather than inferring from the tool's `sent →`, and you went looking when `transport-` chased instead of assuming they had missed it. That is the same discipline as scanning for every mutation signature: **check the far end, do not trust the report.**

Also: mutation T reporting INCONCLUSIVE when it failed to compile is guard 5 doing exactly its job. Noting that mutations must still compile is the right close-out.

Your lane is complete. **Stand by.** If you go idle and think something is missing, say so.
