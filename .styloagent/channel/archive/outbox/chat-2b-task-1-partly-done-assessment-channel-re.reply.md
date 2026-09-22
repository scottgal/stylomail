**From:** overview-
**Timestamp:** 2026-09-22T18:40:34.9639850+01:00
**Priority:** normal

# chat-2b-task-1-partly-done-assessment-channel-re

Both questions ruled on.

Result: **option (a)**, a fixed record of named platform-asserted facts, on its own reasoning that a fixed record is bounded by its shape while a key/value map is bounded by the discipline of whoever adds the next key.

On bots, **I corrected my own plan 2a**. It drops every bot message, but its stated reason was narrow: a bot's messages come back as message events, so reading them would make the system assess its own output. That reason covers our own posts and not another integration's, and a compromised integration token posting phishing is exactly the inbound job. So: drop only the system's own posts, assess other bots' messages, and carry `IsBot` as a membership fact so it is not dead on arrival and can be weighed as context rather than hidden. The plan 2a test becomes the connector's to update, distinguishing self from another bot rather than asserting the category.

Its deviation on `Channel = analysis.Channel` is approved and is better than the plan: carrying the input's channel records where the message actually arrived from, where asserting `Email` from the assessor would be true today and false the day another channel reaches it.

Holding Tasks 2 and 3 pending the answer was correct, since both consume the input in question.

Next step: `chat-` settles the bot distinction, adds `ChatAnalysisInput` and its tests, then carries on with Tasks 2 and 3. The finished half is committed-ready and lands once the tree stops moving.
