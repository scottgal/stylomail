**From:** adaptive-
**Timestamp:** 2026-09-22T14:36:18.3206470+01:00
**Priority:** normal

# Recipient tracking green — plus one record gap and one consequence of the truncation rule you should see

Built and green. **167/167 in my lane**, zero warnings, solution builds. The rule you said matters most is implemented and mutation-verified. Two things need you.

## What landed
`RecipientHistory`: capacity **256**, window **30 days**, both marked unvalidated in the code, hashed keys supplied by the caller, first- and last-seen timestamps, pruned on write. `ProfileObservation` gained an additive nullable `RecipientKeys`, and the encoder now fills `DistinctRecipientsLastHour`, `DistinctRecipientsLast30Days` and `RecipientsNovelToSender`.

**The rule you singled out is implemented and pinned three ways:** novelty is `null` once the set saturates for room, once an entry ages out of the window, and it never recovers. Mutations: answering novelty from a saturated set → 3 RED; aging out without flagging → 1 RED; eviction without flagging → 5 RED; distinct-ness replaced by address count → 1 RED; unknown recipients reported as zero novelty → 1 RED.

I also kept `FanoutLastHour` as **addresses**, not distinct people, and that turned out to be worth more than deleting it: 100 addresses to 2 people and 100 addresses to 100 people are the same number for that field and very different for `DistinctRecipientsLastHour`. The pair is the signal and the gap between them is the fan-out.

## 1. Friction: the record cannot say "floor"
You asked me to "report the count **and** that it saturated". **`BehaviouralProfile` has no field for the second half**, so the count is currently emitted as a floor that *looks* like a measurement. I have documented it in the encoder and added a test that pins today's behaviour and explicitly says to delete it when a flag lands, rather than leaving a silent mismatch between the doc and the wire.

I chose to emit the floor rather than `null` while I wait, and the reason is the opposite of the novelty rule: **a count can only under-state when truncated, and under-stating cannot manufacture alarm.** Novelty over-states, so it goes null. If you would rather it go null too until the flag exists, say so — it is a one-line change.

**Request: add something like `bool RecipientDistinctnessIsFloor` to the record.** `assess-`'s cache-key tripwire asserts the encoder's field count matches Core's, so it will fail their build with "encode it" rather than silently leaving the key blind — the tripwire doing exactly its job.

## 2. A consequence of the truncation rule that I want you to see before it ships
Truncation is **permanent and unrecoverable**, so asking "is this recipient new to this sender?" is answerable only for the narrow band of senders whose *entire* recipient history fits both the window and the cap. An established sender — more than 256 distinct recipients in 30 days, or merely some recipients dormant for a month — loses novelty **forever**, and the counter-case is not hypothetical: that is the compromised-account shape this signal exists to catch.

Pinned by `NoveltyDoesNotRecoverOnceTheHistoryHasBeenTruncated`. I think the rule is right and I am not arguing to weaken it — a permanent "unknown" is honest, and the alternative manufactures the alarm. But the three ways out are a genuine decision rather than a detail:

- **Widen the window** — the cap is the tighter bound in the common case, so this buys little on its own.
- **Accept it.** Distinct-recipient counts still work as floors for exactly these senders, so the classifier is not blind to fan-out; only novelty is lost.
- **Use a structure whose absence is reliable** — a Bloom filter's "not present" is never a false negative, so it can answer "have we ever seen this recipient?" under a memory bound with no truncation at all. Its errors run towards *missing* novelty, which is the safe direction here. It would need your call, and it is my lane to build.

I lean towards the third, on the same reasoning that made the null decision right: pick the structure whose failure mode is the harmless one. But it is a threshold-and-policy change on an interface `assess-` has already keyed on, so I am not making it unilaterally.

## 3. For `assess-`
`ProfileObservation.RecipientKeys` is new, additive and nullable. **Novelty stays null until they supply it** — the encoder will not invent it. I have told them.

Next: holding on both questions. Everything else is green and idle.
