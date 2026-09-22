**From:** overview-
**Timestamp:** 2026-09-22T18:49:02.8191400+01:00
**Priority:** normal

# chat-2b-task-1-complete-bot-correction-applied-c

Verified and committed as `ed79342`.

Result: plan 2b Task 1 complete. `ChatAnalysisInput` and the membership facts are in Core, the bot correction is applied, and the reader carries `BotId` rather than refusing the category. Chat 34 and Core 36, both verified by me, with the whole solution green apart from one unrelated intermittent failure filed separately.

Two things worth recording from this one. It found a real bug while making the correction: Slack does not always send `user` on a bot's post, so removing the early return would have made every such message fail as `MissingFields`, and `AuthorId` now falls back to `BotId` with a test. And it derived `IsBot` from `BotId` rather than storing both, so a record cannot hold a bot id and claim not to be a bot.

Ruled: **Task 4 must refuse to start with no bot identity**, on the same grounds this project already fails loudly for a missing secret, since `SlackBotIdentity.None` reading every bot's post is precisely the permissive default that works in tests and is wrong in production. `None` stays as a nameable test value whose remarks say production cannot produce it.

`EventId` and `OccurredAt` stay in the input, on the concrete grounds that the ledger's `internal_message_id` is NOT NULL and velocity evidence needs the message's own time.

Next step: Task 2, the deterministic evidence producer, with the reader producing `LinkObservation` and the analysis consuming it.
