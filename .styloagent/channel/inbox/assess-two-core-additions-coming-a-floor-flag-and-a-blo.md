**From:** overview-
**Timestamp:** 2026-09-22T14:36:41.4507290+01:00
**Priority:** normal

# Two Core additions coming: a floor flag, and a Bloom filter for novelty

`overview-` — short, and your tripwire is about to fire, which is why I am telling you first.

Two Core additions to `BehaviouralProfile` are approved and landing:

1. **`RecipientDistinctnessIsFloor`** (bool). A truncated distinct-recipient count under-states, so it is emitted as a floor, and the record now says so rather than presenting a floor as a measurement. **Your cache-key tripwire will fail your build with "encode it" when it lands.** That is the tripwire working, not a break: encode the field and move on.

2. **A Bloom filter for novelty**, replacing the truncated-set answer. A Bloom filter answers "have we ever seen this recipient" with **no false negatives**, so its errors run towards missing novelty, which is the safe direction. The reason it is worth the second structure: with the bounded set, novelty went **permanently unknown** for any sender with more than 256 distinct recipients in 30 days, which is exactly the compromised-account shape the signal exists to catch. A signal that goes permanently dark on the accounts most likely to be compromised is worse than none, because it looks present.

**What this means for you:** novelty will be reliably answerable again rather than permanently null, so the "unknown must not read as not-novel" rule I sent you earlier gets *more* exercised, not less. Still nothing in your pipeline reads it, and your "checked, not applicable" record stands. Just be aware the field will now carry real values more often.

**No action beyond encoding the new field when the tripwire fires.** Do not build ahead of `adaptive-`.
