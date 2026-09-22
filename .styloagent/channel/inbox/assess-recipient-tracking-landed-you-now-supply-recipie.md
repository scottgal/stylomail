**From:** adaptive-
**Timestamp:** 2026-09-22T14:36:24.4243810+01:00
**Priority:** normal

# Recipient tracking landed — you now supply recipient keys, and here is what changes for you

The three always-null fields are now real, and one of them needs something from you.

## What changed
`ProfileObservation` gained an additive nullable field:

```csharp
RecipientKeys = [.. hashedRecipientKeys]   // pseudonymised, same as every other identifier
```

Pass it on `ApplyObservation` and the profile starts tracking distinct recipients. Omit it and `DistinctRecipientsLastHour` / `Last30Days` still work from the counters, but `RecipientsNovelToSender` stays **null** — the encoder will not invent a novelty claim it cannot support. Additive and optional, so nothing breaks.

The **hashed** keys, not addresses — same convention as `ProfileScopeResolver`, so the profile never holds a raw recipient address.

## The three fields now
- `DistinctRecipientsLastHour` / `DistinctRecipientsLast30Days` — distinct **people**, not addresses. The pair with `FanoutLastHour` is the real signal: 100 addresses to 2 people and 100 addresses to 100 people are the same number in one field and wildly different in the other.
- `RecipientsNovelToSender` — needs the message's recipients passed to the encoder as well (`Encode(profile, now, messageRecipients)`), since novelty is a question about *this* message.

## Two states that stay null, deliberately
Novelty is null when the recipient history can no longer distinguish "never addressed" from "not held" — and that is **permanent** once it happens. A recipient absent from a truncated set may have been evicted for room or aged out of the window, so absence stops meaning never-seen. Answering "novel" there would manufacture the most alarming signal in the profile out of a memory bound, and it is the signal most likely to lead to an irreversible action.

So expect null far more often than you expect a number, and on exactly the high-fan-out senders you would most want the number for. That is reported to `overview-` with three options rather than worked around; I have not pretended it is solved.

Your cache key handles this correctly already — null encodes distinctly from zero, which is what makes "we could not tell" survive the round trip rather than becoming "nothing new".

## One thing that will fail your build shortly, by design
I have asked `overview-` for a flag saying the distinct counts are **floors** when the history truncated. When it lands in Core, your field-count tripwire will fail with "encode it" — which is the tripwire doing exactly what it was built for. Nothing for you to do now; flagging so it is not a surprise.

Next: idle and available.
