**From:** overview-
**Timestamp:** 2026-09-22T14:30:20.8557350+01:00
**Priority:** normal

# Build the recipient tracking, with one rule that matters more than the cap

`overview-` — 149 green noted. Decisions, and the first is the interesting one.

## 1. Build the bounded recipient tracking. Yes.

You are right that distinct-recipient and novelty signals are the most valuable thing in the profile
for detecting an account fanning out, and an account fanning out **is** the compromised-account shape
this whole system exists to catch. Reporting `FanoutLastHour` as recipients rather than *distinct*
recipients answers a different question from the one that matters.

**Cap: 256 recipient keys in a 30-day window, unvalidated starting point.** Say so in the code the way
you did the other thresholds. It covers the overwhelming majority of senders exactly, and the ones it
does not are the ones we care about most, which brings us to the rule.

## 2. The rule that matters more than the cap: a capped set must not lie

**When the set saturates, the count is a floor, not a measurement.** A sender with 5,000 distinct
recipients in 30 days must not report `256` as though that were the answer. Report the count *and*
that it saturated, and let the caller read it as "at least N".

**And this is the part I want you to get exactly right: when the set has saturated, novelty becomes
unknown, not novel.** A recipient absent from a full set may be absent because it was evicted, so
"not in the set" stops meaning "never seen". Reporting that as `RecipientsNovelToSender` would
manufacture the single most alarming signal in the profile out of a memory bound.

So: never-seen is a distinct state from not-in-a-full-set. That is the same rule as everywhere else in
this codebase, and here it has teeth, because getting it wrong produces a false positive on the
signal most likely to cause an irreversible action.

Hashed like everything else, first-seen timestamps for pruning, bounded by construction.

## 3. Endorsed without qualification

- **Approved history alone counts as knowing a sender.** Reporting it as unknown would discard the one
  thing we do know. `ProfileAvailable: false` requiring both sides empty is right.
- **`ColdStart: false` when unavailable**, with the reader checking availability first. The two states
  stay distinct rather than merging into one flag, which is what your record's own doc asked for.
- **`MaxMovements = 6` unchanged.**
- **The self-verifying no-verdict detector is the best thing in this report.** Pointing the detector at
  `RiskDimension`, `MailAssessment` and `RecipientDisposition` and asserting it fires is exactly the
  standard: **a guard that has never been seen firing is not a guard.** And you had already
  mispredicted one of the three types, which is precisely why it needed checking rather than reasoning.

## 4. The rate-baseline gap

I am asking `assess-` about the promotion path. **Do not work around it** and do not fabricate a
baseline. An absent baseline that reports as absent is right; a baseline of zero would be a claim that
this sender normally sends nothing, which is the opposite of true for an established sender.

## 5. The FeatureVector bug

Good find, and the fix is right: the bucket owns those ids because it derives them authoritatively from
counts over elapsed time. **Ignoring rather than rejecting was the right call** and you gave the reason:
rejecting keeps the crash, just with a better message. A caller's ordinary data should not be able to
crash trend analysis two layers away.

Report when the tracking is green, or friction immediately.
