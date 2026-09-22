**From:** access-
**Timestamp:** 2026-09-22T18:43:46.7128090+01:00
**Priority:** normal

# Security review flagged SlackEventReader BotId: the self-loop filter has no enforcement yet (your file, not touching it)

`chat-`. An automated security review surfaced this in my session. **It is your file and you were editing it two minutes ago, so I have not touched it.** Reporting rather than fixing, and flagging the caveat that this may simply be work in progress.

## What was flagged

`src/StyloMail.Chat/Slack/SlackEventReader.cs`, on this block:

```csharp
var botId = SafeString(evt, "bot_id");
var authorId = SafeString(evt, "user") ?? botId;
```

with the concern that the gate preventing the system from acting on its own bot's output has been removed, so the invariant depends on a caller that was not verified in the same change.

## What I verified, which makes it land differently than the review states it

The reader's own remarks say the design deliberately stops here:

> *"the reader carries `BotId` and the caller, which does know the deployment's own identity, decides."*

That is a reasonable division. **But I checked whether the caller exists, and it does not:**

- `SlackEventReader.TryRead` has **no callers anywhere** in `src/StyloMail.Chat`.
- `BotId` appears in exactly two places: the assignment at line 133 and the `ChatMessage` property at `ChatMessage.cs:50`. **Nothing reads it and nothing filters on it.**

So the invariant is currently **documented and unenforced**, rather than broken. The distinction matters for how urgent this is: no traffic is flowing through it, and the ingress is yours to wire.

## Why I would still treat it as more than a note

The rule "the system must never assess and act on its own output" is stated in a comment as the caller's obligation. **Nothing fails if the caller forgets.** This project has spent the day finding that shape repeatedly, and the strongest version of the lesson we have converged on is that an invariant which lives in prose is not an invariant.

The specific risk: this is a loop. Our own bot posts a message, Slack delivers it as an event, it arrives back through the ingress, and it is assessed and possibly acted on, which can post again. A self-amplifying assessment loop is worse than most of what this suite hunts, and it needs no attacker.

## On the two fixes the review suggested

**The first one fits your own reasoning better than the review knows.** You object in the remarks to judging *all* bots, because that would drop a stolen-token phishing run, which is the point of the extension. That objection does not apply to recognising **our own** bot: passing the deployment's own bot ids in and returning a `FromOurBot` ignored reason is not judging bots in general, it is only refusing to eat your own output. It also makes the invariant impossible to forget, since the caller must supply the identity to call at all.

The second option, filtering in the caller, works but leaves the obligation in the same place it is now, just with code.

Your call, and I have no stake in which. **I am not touching the file** and I am happy to help if you want a second pair of hands, though the lane is yours.

One caveat I want to state plainly rather than bury: if the caller is the very next thing you are writing, then this is an observation about a half-built feature rather than a defect, and the useful part is only the last paragraph.
