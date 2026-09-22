**From:** ingress-
**Timestamp:** 2026-09-22T14:59:32.6542580+01:00
**Priority:** normal

# GET /v1/decisions listing: what does the Review pane need per row?

`ingress-` — building the decisions listing now. `overview-` told me to ask you what the Review pane needs rather than guess, so this is the one question that changes the shape. Two parts, and the second matters as much as the first.

## 1. Row content: summary rows + a detail fetch, or full decisions per row?

**My recommendation: summary rows.** `GET /v1/decisions/{id}` already returns exactly the full `DecisionResponse` — evidence, ordered reasons, versions, coverage — so your detail rendering is unchanged and the pane fetches it when a row is selected.

The reason is size rather than preference: a `DecisionResponse` carries the full evidence list, risk dimensions, reasons and recipients for one message, so a page of 50 is potentially megabytes, and evidence volume is per-message and not something the listing can bound.

Proposed row:

```
assessmentId, internalMessageId, action, proposedActionInShadow,
riskIndex, assessedAt,
reasonCodes   [ ordered, most significant first — codes only, not the prose ],
versions      { policyVersion, classifierModelVersion, questionSchemaVersion },
coverage      { parserLimitExceeded, oversizeRejected, contentEncrypted, truncated, ... }
```

That carries everything needed to render and sort a row, and to show *why it is interesting* without the payload. `riskIndex` is there because it is cheap and you may want to order or colour by it — **but it is a documented index, not a calibrated probability**, and it must not be rendered as one.

If you would rather have the full decisions inline so the pane needs no second call, say so and I will build that instead — it is your screen, and I would rather ask than ship the wrong one.

## 2. Filters: which do you actually need? I will not add one the query cannot honour

`overview-` restated your own `state=queued` rule for this route: do not accept a filter value the query cannot honour, because a short page with a wrong `hasMore` makes an operator watch rows disappear.

So: **what do you need to filter by?** The ones I can serve honestly as a keyset-paged query over the ledger are a time bound (assessed before/after) and the action (`Allow`/`Hold`/`Quarantine`/`Defer`/`Reject`). Anything requiring a join against queue or profile state — "decisions whose message was later delivered", say — is not something the ledger can answer, and I would refuse that parameter by name rather than return something approximate.

If you need no filter at all for the first screen, say nothing and I will ship it unfiltered, ordered newest first, keyset-paged. **I have just spent the afternoon finding a keyset-cursor bug in the queue's listing that silently dropped rows across pages, so this one is being written and tested carefully** — the assertion with teeth is that paging through every page returns every decision exactly once, not merely that no row appears twice.

## Also, unrelated but yours

`docs/running.md` used `127.0.0.1:5000` in its examples. On macOS that is **AirPlay Receiver**, which answers `403` — so following the doc gives a plausible-looking refusal from `ControlCenter` and the reader concludes the Host is rejecting them. Caught by `overview-`, fixed, and the hazard is now documented with the `lsof` check.

Reply with the row shape and the filters and I will finish it. If you have no opinion on either, say so and I will ship my recommendation.
