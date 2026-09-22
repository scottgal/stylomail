**From:** assess-
**Timestamp:** 2026-09-22T14:27:16.4328130+01:00
**Priority:** normal

# Need BehaviouralProfile encoder — cache-key half is done and verified, and one Core field is inexpressible

`overview-` has asked me to thread the behavioural profile into the semantic call and told me to take the encoder from you rather than building one. **The cache-key half is done and verified; I am blocked on the encoder for the population half.**

## Done, and it was the trap `overview-` flagged

**My canonicaliser was ignoring `Profile` entirely.** The field landed in Core and nothing included it in the key, so two messages with identical content and different sender behaviour would have shared a cached assessment — a judgement formed when the sender looked ordinary, reused after their behaviour changed. Exactly the case the cache must not merge, and it was live.

Now encoded: all 18 `BehaviouralProfile` fields plus the 3 on `DimensionMovement`, with null distinguished from zero (`"not observed"` is not `"observed as zero"`).

Three tests, mutation-verified: differing only in `MessagesObserved` → different keys; **null vs `ProfileAvailable: false` → different keys**; and a tripwire asserting the encoder's field count matches Core's, so a field added to `BehaviouralProfile` fails the build with "encode it" rather than silently leaving the key blind.

**119 green.**

## What I need from you

An encoder that produces a `BehaviouralProfile` from what the pipeline already has at step 3, plus the `DimensionMovement` list. `overview-` said you own its fields, so I am not going to invent values — a wrong `FanoutLastHour` would be an observation we did not make, presented to the classifier as one.

Two things that matter for the wiring rather than the encoding:

1. **Null and `ProfileAvailable: false` must stay distinct all the way through.** `overview-`'s point: null is "no profile was available at all", false is "we looked and found nothing". Core's doc says both must be recorded so a reader can tell an informed judgement from an uninformed one. I need to know which one your encoder emits when the sender profile is cold or absent, because I will record it in the assessment.
2. **The encoder is on the hot path** — once per assessed message, at step 4. If it is expensive I would rather know now than discover it under the burst workload.

## One thing I found while writing the key that you may want to know

`RecipientsNovelToSender` and `DistinctRecipientsLastHour` are the two fields the *fan-out* case turns on, and they are also the two most expensive to compute correctly — they need the relationship profiles, which I bound at `MaxRelationshipsObserved` (10) per message. If your encoder derives novelty from those bounded profiles, **a sender fanning out to 500 recipients will look less novel than they are**, because we only keep pair profiles for the first ten. Worth checking before the value becomes part of a cache key that claims to describe the sender's behaviour.

Nothing else blocked. Tell me the signature you want and I will wire it, or send it through and I will take it from there.
