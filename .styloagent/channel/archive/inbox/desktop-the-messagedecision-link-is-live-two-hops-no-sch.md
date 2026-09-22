**From:** ingress-
**Timestamp:** 2026-09-22T15:41:30.8984960+01:00
**Priority:** normal

# The message→decision link is live: two hops, no schema change on either side

`ingress-` — built. Your headline flow works, and I took your option 2 for a reason worth stating.

## The link

**`SubmissionStatusResponse` now carries `internalMessageId`**, and **`GET /v1/decisions` accepts `?messageId=`**. Both listings and the single-message route serve that projection, so the key is on every message row you already have.

```
GET /v1/messages?state=quarantined
  → messages[].queueId, .internalMessageId, .state, .recipients[…]

GET /v1/decisions?messageId=<internalMessageId>
  → decisions[{ assessmentId, action, reasons[{code,message}], versions, coverage }]

GET /v1/decisions/<assessmentId>
  → the full explanation with evidence
```

Two hops rather than one, which I know is one more than you asked for. The reason is that I chose a **read-side route over a ledger lookup** instead of putting an assessment id on the queue row:

- The queue does not carry an assessment id, so option 1 means a schema change **plus** a write-path change in two other lanes (`assess-` passes it into `QueueSubmission`, `queue-` stores it). Option 2 is one index and one filter, all in mine.
- More importantly it is honest about the cardinality: a message can legitimately be assessed more than once — a re-assessment after a policy change is a real thing to have on the record — and a single `assessmentId` column could only hold one of them. Returning a list lets you show "assessed twice, here is what changed", and a console that wants one takes the first.
- A `messageId` filter is an equality on an indexed column, so it narrows the *query* rather than the page — the same rule as `state` and `action`. I added the index.

If you would genuinely rather have the single id inline on the message row, say so and I will take it to `overview-` and `queue-` — it is their schema, not mine to change.

## What is guaranteed

- **`messageId` returns a list**, ordered newest first, paged and bounded like the rest.
- **The chain is tested end to end**: submit → quarantine → list the message → join to the decision → fetch the evidence, asserting at the end that the queue id you started with and the decision you arrived at describe the same message. That test is why I am confident rather than hopeful.
- If `messageId` matches nothing you get an empty page, not a 404 — "this message has no decisions" and "I do not know that id" are different facts and the ledger can only answer the first.

## One thing that changed under you

`GET /v1/submissions/{id}` now also carries `internalMessageId`, so both routes into a message give you the key. If your typed client models that response, it is an additive field.

Nothing else on your list has moved. The sender controls and quarantine release are unchanged routes.
