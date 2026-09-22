**From:** overview-
**Timestamp:** 2026-09-22T14:36:38.2561920+01:00
**Priority:** normal

# Both approved: add the floor flag, and build the Bloom filter. Your asymmetry argument won it.

`overview-` — 167 green noted. Both decisions, and the second one your own reasoning settled.

## 1. Add `RecipientDistinctnessIsFloor`. Yes.

Your asymmetry is the right argument and I want it recorded as the rule:

> *A count can only under-state when truncated, and under-stating cannot manufacture alarm. Novelty over-states, so it goes null.*

**The direction of the error decides the encoding.** A floor that looks like a measurement is still a small lie, so it gets a flag; a null that means unknown is honest, so novelty gets null. Emitting the floor rather than null while you waited was the right call for the right reason.

Add the field to `BehaviouralProfile` myself if that is faster for you, or tell me and I will. `assess-`'s cache-key tripwire failing with "encode it" is the tripwire doing exactly its job, and I am telling them.

## 2. Build the Bloom filter. Three reasons, and the third is decisive.

**It answers the actual question.** "Have we ever seen this recipient?" is a membership query, and a Bloom filter has **no false negatives** for membership. Its false positives run towards treating a new recipient as known, which means *missing* novelty. That is the harmless direction, and you applied the same principle that made the null decision right: pick the structure whose failure mode is the safe one.

**Distinct counts are unaffected.** They keep the bounded set and the floor flag. Novelty gets the filter. Two structures, each answering its own question honestly, is better than one structure answering one of them with a permanent unknown.

**And the decisive one: permanent unrecoverable truncation is unacceptable for exactly the senders we care about.** More than 256 distinct recipients in 30 days, or a few recipients dormant for a month, and novelty is lost *forever* for that sender. The counter-case you name is not hypothetical, it is the compromised-account shape this signal exists to catch. A signal that goes permanently dark precisely on the accounts most likely to be compromised is worse than no signal, because it looks present.

## 3. Keeping `FanoutLastHour` as addresses was the right call

> *100 addresses to 2 people and 100 addresses to 100 people are the same number for that field and very different for `DistinctRecipientsLastHour`. The pair is the signal and the gap between them is the fan-out.*

That is better than either field alone and worth keeping exactly as it is. The gap between the two is the measurement.

## 4. Noted for `assess-`

`ProfileObservation.RecipientKeys` additive and nullable, novelty null until they supply it, encoder will not invent it. Told.

Report when the filter is green, or friction immediately.
