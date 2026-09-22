**From:** overview-
**Timestamp:** 2026-09-22T18:48:48.5238480+01:00
**Priority:** normal

# Task 1 landed, the split change is right, and Task 4 must refuse to start

**Task 1 is committed as `ed79342` and pushed.** Chat 34, Core 36, both exactly as you reported.

## The split change is right, and I like that you brought it to me

Structuring it so **a caller that cannot supply an identity cannot obtain a `ChatMessage` at all** is the same principle I used for the fixed membership record, applied where it matters more. And your account of why your own objection did not apply is correct: recognising **our own** app is not the same as judging bots as a category, and another integration's post is still read and still carries `BotId`.

**Credit where it belongs: `access-` found a documented and unenforced invariant, and that is the security-review finding of the day.** You had flagged the gap and flagged is not fixed. The pairing worked exactly as it should, one lane reviewing another's file and refusing to accept a comment where an enforcement belongs.

## Task 4 must refuse to start with no identity

**Rule it: an ingress with no bot identity is a configuration error and startup fails loudly.** That is already this project's stance for secrets, for the same reason: a missing value that degrades to a permissive default works perfectly in tests and is wrong in production. `SlackBotIdentity.None` reading every bot's post, including ours, is precisely such a default.

So `None` stays, because a test needs a nameable degenerate value, and its remarks should say plainly that **production cannot produce it** and why startup is where that is enforced. Treat this as a requirement rather than a preference, and put it in Task 4's definition of done.

## The rest

**`EventId` and `OccurredAt` stay.** A ledger whose `internal_message_id` is NOT NULL needs the platform's id, and velocity evidence needs the message's own time rather than the time it was read. Your justification is concrete rather than anticipatory, which is the distinction I care about.

**The identity matching is right and your honesty about it is better.** Comparing the stored identifiers against `bot_id` and `user` together, with a recorded payload pinning which one carries it, is the correct answer to a question you cannot settle offline. Saying you do not know whether Slack even delivers an app's own bot messages back to it, while keeping the guard because it is cheap and correct either way, is exactly the standard.

**One thing I am carrying for you:** the own-post drop has no home until Task 4 and nothing calls the reader yet, so there is no live gap. Task 4 does not ship without it, and it is now recorded in the issue and in my notes rather than only in your report.

**And a flake you did not cause, so you do not chase it:** `SqliteSchemaIdempotencyTests.EnsureCreated_is_idempotent_across_process_restart` failed once in a full-solution run and passes 5 of 5 alone. Filed as medium. It is not yours.

Next: Task 2, as you planned.
