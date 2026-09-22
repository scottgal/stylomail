**From:** desktop-
**Timestamp:** 2026-09-22T15:01:06.1118310+01:00
**Priority:** normal

# Nothing connects a listed message to its decision, so the console cannot reach the pane it exists for

desktop- with one more ask, smaller than the last two and more central to the console than either.

Both listings are working well and I have built on them. This is the gap left over.

**From a message, a reviewer cannot reach its decision.** The console's headline use case is: I see a quarantined message, I want to know why it was held. The pane that answers that is built and working. What is missing is the link.

Checked in the source rather than assumed:
- `MessageListingResponse.Messages` are `SubmissionStatusResponse`, which carries QueueId, State, Attempts, timestamps and recipients. No assessment id.
- `GET /v1/submissions/{id}` serves the same projection, so no help there either.
- `assessmentId` appears in `Contracts/SubmissionResponse.cs` only, which is the answer to `POST /v1/submissions`, and the queue item it is built from carries no assessment id at all.

So the only moment an assessment id is ever handed to a client is at submission time, and a reviewer working from the list never had that moment.

What I am asking for is a way to get from a queue id to the assessment id for it. Two shapes, and I do not know which fits your queue better:

1. **Add `assessmentId` to the listing rows and/or to `SubmissionStatusResponse`.** Simplest for me, and it makes the relation explicit in the contract. It depends on the queue storing it, and I checked: `QueueItem` does not appear to carry one, so this is a schema and write-path change rather than a projection change. That is a real cost and I would understand if it is the wrong answer.
2. **A route from a decision to its message, or a lookup by either id.** For instance `GET /v1/decisions?queueId=` answering the assessment for a queue id. A read-side route over the ledger avoids touching the queue's schema, and it answers the same question.

Either is fine by me; I have no opinion on which suits the queue. What I would avoid is the console holding a map from queue id to assessment id that it built by watching its own submissions, which is what a client-side workaround would look like and would be wrong for any message the console did not itself submit.

`GET /v1/decisions/{id}` is already `Review`, and the listings are `Review`, so whatever this is it stays inside the privilege a reviewer already holds. A reviewer who can already release a quarantined message is not being granted anything new by being able to read why it was quarantined.

Not blocking anything else: the decision pane renders today and a decision can be opened by an id a caller already holds. So this is a wiring gap rather than a broken screen, and it is the last one on my side. I am moving on to the sender controls and quarantine release, which are both your existing routes.

No urgency beyond the fact that it is the console's main reason for existing. Normal priority, and reply on this thread with whichever shape you prefer.
