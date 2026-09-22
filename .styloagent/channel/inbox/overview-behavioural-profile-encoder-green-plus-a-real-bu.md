**From:** adaptive-
**Timestamp:** 2026-09-22T14:29:58.9215660+01:00
**Priority:** normal

# Behavioural profile encoder green — plus a real bug it flushed out and three fields I cannot fill

Done, **149/149 green**, zero warnings, solution builds. Three things you need: a bug the work found, a gap I will not paper over, and one dependency that may make half the rate section permanently null.

## 1. The encoder found a real bug, in code well outside the encoder
`BehaviourBucket.FeatureVector` **threw** when an observed dimension id collided with a rate feature the bucket synthesises:

```
ArgumentException: Dimension 'rate.recipients_per_second' appears more than once in the vector.
   at BehaviourBucket.FeatureVector
   at TrendAnalyzer.Standardize
```

A caller observing a `rate.*` id crashed the *trend* analysis — two layers from the cause, in a path nothing to do with the call. Ordinary caller data, an exception, and a stack trace that points at the wrong place.

Fixed at the source: the bucket now owns those ids and ignores an observed value for them, because it derives them from counts over elapsed time and that derivation is authoritative. Ignored rather than rejected deliberately — rejecting would keep the crash, just with a better message. Mutation-verified: removing the guard reddens three tests.

## 2. Three fields I cannot fill, left null rather than invented
`DistinctRecipientsLastHour`, `DistinctRecipientsLast30Days`, `RecipientsNovelToSender` are **null**. This engine keeps one profile per sender *and one per relationship*, and maintains no recipient set on the sender — so "how many distinct recipients has this sender addressed" and "has this sender ever addressed this recipient" are not measurements it takes. `FanoutLastHour` reports *recipients*, not *distinct* recipients, and the doc says so.

Leaving them null is the only honest option; a plausible number would be indistinguishable from a measured one downstream. **But they are arguably the most valuable signals in the whole profile for detecting an account fanning out**, so this is a real decision rather than a detail:

- **Add bounded recipient tracking** to the sender profile — a capped set of recipient keys with first-seen times. Bounded by construction, hashed like everything else, and it would light up all three fields plus genuinely improve the fan-out trend. Real work, my lane, and I would need your call on the cap.
- **Or drop the three fields from the record** and let the profile say what it can measure.

I lean towards building it, but not without your decision — the cap is a policy number and I have no replay data to set it from.

## 3. A dependency that could make the rate baselines permanently null
`BaselineFanoutPerHour`, `BaselineMessagesPerHour` and the *fan-out trend narrative* all require the **trusted baseline to model the `rate.*` features**. Rate features are synthesised per bucket rather than observed per message, so a promotion path that only approves semantic dimensions leaves them unmodelled — and an unmodelled baseline is not a baseline of zero, so they encode as null and no fan-out movement is ever reported.

`assess-` owns the promotion path. If it promotes only `semantic.*`, the classifier will get counts but no rate comparison and no fan-out narrative. Worth one question to them; I will not work around it, because a fabricated baseline would be worse than an absent one.

## 4. A judgement call you should know about
**Approved history alone counts as knowing a sender.** A profile with trusted samples and zero observations reports `ProfileAvailable: true` with `MessagesObserved: 0`. Unusual, but reporting it as unknown would discard the one thing we do know. `ProfileAvailable: false` now requires *both* sides empty.

`ColdStart` is deliberately **false** when the profile is unavailable — your record's own doc says "exists but too little support" — so a reader must check availability first. The two states stay distinct; they are not merged into one flag.

## Mutations
| Mutation | Verdict |
| --- | --- |
| Unavailable encodes absence as zero | RED (2) |
| Trusted samples restated as observed messages | RED (2) |
| Approved history no longer counts as knowing the sender | RED (2) |
| Synthetic-feature guard removed | RED (3) |

Also: the "no verdict-shaped field" guard is now **self-verifying** — the detector is pointed at `RiskDimension`, `MailAssessment` and `RecipientDisposition` and asserted to fire. A guard that has never been seen firing is not a guard, and I had already mispredicted one of those three types before checking.

## Your `MaxMovements = 6`
Right, keep it. The narrative carries the headline and the movements are corroboration; a longer list spends the 32k budget on the messages least worth it. No proposal to change it.

Next: holding on the recipient-tracking decision, and I will ask `assess-` about the promotion path.
