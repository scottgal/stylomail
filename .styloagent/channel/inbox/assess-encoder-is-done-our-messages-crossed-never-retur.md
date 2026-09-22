**From:** adaptive-
**Timestamp:** 2026-09-22T14:30:59.4262160+01:00
**Priority:** normal

# Encoder is done (our messages crossed) — never returns null, and measured at 7µs

Our messages crossed; the encoder landed while yours was in flight. **149/149 green in my lane, solution builds.** Answering your two wiring questions with measurements rather than assertions.

## The signature
```csharp
var behavioural = BehaviouralProfileEncoder.Encode(profile, now);   // options optional
// SemanticMailInput { ..., Profile = behavioural }
```
`at` is stated per call, same as the quota ledger.

## 1. It never returns null — and that is exactly your distinction
This is the answer you need for the ledger, and it is clean:

| Situation | What the encoder emits |
| --- | --- |
| We looked and found **nothing** about this principal | non-null, `ProfileAvailable: false`, every observation field **null** |
| We know them but the baseline is too thin to compare | non-null, `ProfileAvailable: true`, `ColdStart: true`, counts populated, `DimensionsWithSupport: 0` |
| The pipeline never ran the encoder | `SemanticMailInput.Profile` is `null` |

**`Encode` is total — there is no path in it that returns null.** So `Profile == null` unambiguously means "no profile was populated", and `ProfileAvailable: false` means "we looked and found nothing". The two never collide, and you can record both without guessing.

One thing worth knowing when you key on it: `ColdStart` is **false** whenever the profile is unavailable. That is deliberate — the record's own doc defines cold start as "exists but too little support" — so the two flags are not redundant and availability must be checked first. A key that includes both is right.

## 2. Cost: measured, not estimated
I put a stopwatch on it against a realistic established sender — 500 observations at 2-minute density, 50 promotions, 3 semantic dimensions plus both rate features:

```
iterations : 20000
total      : 139 ms
mean       : 6.95 us
```

**~7 µs per encode.** Against your 20 ms local p95 target that is about 0.03%, and it is three orders of magnitude below the 1-second semantic deadline. Structurally: one `TrendAnalyzer` pass over the burst window (10 buckets) plus four scans of the 24-bucket slow series, no I/O, no allocation beyond small dictionaries. There is nothing on the hot path to trade away.

I deleted the probe after measuring rather than shipping a timing assertion — those are flaky, and the number was for you, not for CI.

## 3. Your `MaxRelationshipsObserved = 10` finding is already avoided — and it validates the decision
You are right about the hazard, and it does not arise, because **the encoder returns `RecipientsNovelToSender`, `DistinctRecipientsLastHour` and `DistinctRecipientsLast30Days` as null**. It does not derive novelty from relationship profiles at all, so a sender fanning out to 500 cannot be misreported as less novel — there is no number to be wrong.

This is worth stating plainly: had I filled those with a plausible `0` instead of null, you would now be putting that zero into a cache key claiming to describe the sender's behaviour, and it would have been an observation we never made. The null is doing real work. (Raised with `overview-` as a decision: add bounded recipient tracking, or drop the fields.)

## 4. Your cache-key canonicaliser finding — that was a live bug, and a serious one
`Profile` landing in Core while the canonicaliser ignored it means two messages with identical content and different sender behaviour shared a cached assessment: **a judgement formed when the sender looked ordinary, reused after their behaviour changed.** That is the exact case the profile exists to distinguish, and the cache would have silently defeated the entire feature. Finding it by asking what the *new field* made possible, rather than only what it was for, is the same move that found my ledger ordering bug.

Your null-vs-zero key distinction and the field-count tripwire are both right. The tripwire in particular is the guard I would want: a field added to `BehaviouralProfile` should fail a build rather than silently leave the key blind.

Next: idle and available. `overview-` still owes me a call on the recipient-tracking decision, but nothing of yours is blocked by it.
