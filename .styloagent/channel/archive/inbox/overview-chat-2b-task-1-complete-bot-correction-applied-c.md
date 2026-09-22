**From:** chat-
**Timestamp:** 2026-09-22T18:42:36.3857720+01:00
**Priority:** normal

# chat- 2b Task 1 complete: bot correction applied, ChatAnalysisInput in, and how the connector knows its own bot identity

Task 1 is complete, tested first and frozen. Both rulings applied, and here is the identity answer you asked me not to assume.

## The bot correction

**The reader now observes which bot posted rather than judging the category.** `ChatMessage` gained `BotId` (null for a person), and `SlackEventReader` no longer refuses bot messages. That is structural decision 8 doing the work: "is this bot us?" needs the deployment's identity, which this layer does not hold, so the fact is carried and the caller decides. Judging here would also have dropped a stolen-integration-token phishing run, which is the inbound job.

**`SlackEventIgnored.FromABot` is deleted.** With the reader no longer refusing bots it is unreachable, and a dead enum member saying "we drop bots" would misstate the policy to the next reader, which is the same defect I flagged in `MissingSignature` on day one.

**A real bug I found while doing it.** Slack does not always send `user` on a bot's post, because the platform has attributed it to the bot. The old code returned early on `bot_id`, so that never mattered; removing the early return would have made every such message fail as `MissingFields`. `AuthorId` now falls back to `BotId` when `user` is absent, and there is a test for it.

Tests updated as you said: the old "the system's own posts are ignored" test is gone, replaced by three that distinguish another bot's post (read, carrying `BotId`), a bot post with no `user` (attributed to the bot), and a person (no `BotId`).

**One thing to be aware of:** the own-post drop now has no home until Task 4, because Task 4 is where the identity lives. Nothing calls the reader in production yet, so there is no live gap, but Task 4 must not ship without it.

## How the connector knows its own bot identity

**From the install, not from a call on the message path.** Slack's OAuth v2 access response returns the installing app's own `bot_user_id` and `app_id`, and `auth.test` returns the authenticated bot's ids. Both are configuration the deployment already holds as part of installing the app, so the comparison costs nothing per message. That answers your condition: it is obtainable without a call I would rather not make.

**What I cannot verify offline and will not assert.** Slack's `message` event carries `bot_id` (a `B...` identifier) while the install response gives `bot_user_id` (a `U...` identifier), and a bot's post may also carry `user`. Which field identifies the posting app in every case, across bot-published posts, app-published posts and Enterprise Grid, is a fact about the platform's payloads rather than something I can settle by reasoning. Plan 2a's stated testing strategy is recorded event payloads for exactly this reason. So **Task 4 should compare the deployment's stored bot identifiers against the event's `bot_id` and `user` together, and a recorded payload should pin which one carries it**, rather than my asserting a single field from here.

A second thing I cannot confirm offline: whether Slack delivers an app's own bot messages back to that app at all. The guard is cheap and correct either way, so it stands regardless, but I would rather say I do not know than have it read as verified.

## ChatAnalysisInput, option (a)

**new `src/StyloMail.Core/ChatAnalysisInput.cs`**: `ChatAnalysisInput` and `ChatMembershipFacts`. The membership record is a fixed set of named facts, `AuthorId` and `BotId`, with `IsBot` **derived** from `BotId` rather than stored, so a record cannot hold a bot id and claim not to be one. Your `IsBot` is carried exactly as you asked.

**Two members beyond your list, both flagged.** The design lists `ChannelContext`, `BodyText`, `Links`, `ConversationContext` and membership. I also carry `EventId` and `OccurredAt`, because Task 3 cannot be written without them: the ledger's `internal_message_id` is NOT NULL and needs the platform's id, and velocity evidence needs the message's own time rather than the time it was read. If you would rather they arrive in Task 3, they are two lines to move.

**One test pins decision 8 at the type level**: `Links` is `IReadOnlyList<LinkObservation>`, so a later edit that made the connector produce `LinkFinding` would fail the suite rather than quietly move a judgement into the reader.

## Measured on the frozen tree

`dotnet build StyloMail.slnx`: 0 warnings, 0 errors.
`dotnet test StyloMail.slnx`: **1371 passed, 0 failed, 23 skipped.** Chat 30 (one test replaced by three), Core 36 (+4). Assessment 124, Mime 91, Host 305 all unchanged. Every other project's count is unchanged from the half you are about to land.

Next: Task 2, the deterministic evidence producer, with the reader producing `LinkObservation` and the analysis consuming it. I am keeping 2 and 3 apart as agreed. Note that the tree is now carrying both uncommitted halves of Task 1.
