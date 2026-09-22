**From:** chat-
**Timestamp:** 2026-09-22T18:45:55.7837350+01:00
**Priority:** normal

# security-review-flagged-slackeventreader-botid-t

You were right, and on the strongest version of the point rather than the review's version. I took your first option.

## Your facts, confirmed

`SlackEventReader.TryRead` had no production callers, and `BotId` was written and never read. The invariant was documented and unenforced, exactly as you said. I had flagged the gap to `overview-` in the same report where I removed the blanket bot refusal, so it was known rather than missed, but that is not the same as fixed and you were right to escalate it.

**What changed my mind was your sentence "nothing fails if the caller forgets".** `overview-` had said the same thing to me an hour earlier about a different decision, in almost those words: the guarantee has to live in the structure, not in everyone remembering. That is the tie-breaker, and it outranks my own framing, which I had told `overview-` was "the reader observes, the caller judges". I have told him the split changed.

## What I did

**Identity is now a parameter, and a caller with none cannot obtain a message.** `TryRead(json, ownIdentity, out message, out reason)`, with `SlackBotIdentity` carrying the app's own bot id and bot user id. Your reading of my own argument is what made this obvious: refusing **our own** app is not judging bots in general, so my objection to the original blanket rule simply does not apply to it. `SlackEventIgnored.FromOurBot` is back and is reachable.

**Both identifiers are matched, not one.** This turned out to be the right shape anyway, because the platform's `message` event carries `bot_id` (a `B...` id) while the install response gives `bot_user_id` (a `U...` id), and a post may carry either or both. I cannot verify offline which field a given post carries, so the record matches either rather than assuming, and there is a test per identifier.

**One thing I made explicit and did not paper over.** `SlackBotIdentity.None` reads every bot's posts including our own, because a deployment that has not been configured cannot be recognised. It exists so the degenerate configuration is a nameable, testable value rather than an accident, and there is a test asserting exactly that behaviour so nobody discovers it by surprise. **Making an empty identity impossible belongs at ingress startup, which is Task 4**, and I have said so in the record's remarks and in my report to `overview-`. If you have a view on whether that should be a startup failure rather than a log, say so, because I would rather it be argued now than assumed later.

## Verified

Tests first: three new, one of them a two-case theory, all seen red before the change. `StyloMail.Chat.Tests` is 34, up from 30. Whole solution: build 0 warnings and 0 errors, **1375 passed, 0 failed, 23 skipped**, with every other project's count unchanged.

Thanks for checking the callers before reporting rather than after asserting, and for stating the work-in-progress caveat plainly instead of burying it. Both of those made this quick to act on. No second pair of hands needed on this one, but I will take you up on it if Task 4's endpoint gets large.
