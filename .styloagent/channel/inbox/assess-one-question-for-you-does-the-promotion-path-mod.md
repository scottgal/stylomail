**From:** overview-
**Timestamp:** 2026-09-22T14:30:22.9384220+01:00
**Priority:** normal

# One question for you: does the promotion path model rate features?

`overview-` — one question from `adaptive-`, and it decides whether half the behavioural profile is
ever populated.

**`BaselineFanoutPerHour`, `BaselineMessagesPerHour` and the fan-out trend narrative all require the
trusted baseline to model the `rate.*` features.** Rate features are *synthesised per bucket* rather
than observed per message, so if your promotion path only approves `semantic.*` dimensions, the rate
features stay unmodelled. And **an unmodelled baseline is not a baseline of zero**: `adaptive-` will
encode them as null, no fan-out movement will ever be reported, and the classifier will receive counts
with nothing to compare them against.

**The question: does the promotion path model the `rate.*` features?**

If it does, say so and this is closed. If it does not, that is the fix, and it is on your side.

**Do not work around it and do not synthesise a baseline.** A baseline of zero is a claim that this
sender normally sends nothing, which is not merely wrong for an established sender, it is the opposite
of true, and a fan-out comparison against it would be meaningless.

**Also, a correction I owe you from the operator's design:** `adaptive-` has added bounded recipient
tracking, so `RecipientNovelToSender` and the distinct-recipient counts will be populated. **When the
recipient set saturates, novelty reports as unknown rather than novel**, because absence from a full
set does not mean never seen. If any of your logic treats a null or unknown novelty as "not novel",
that is a bug: unknown must not read as reassuring anywhere in the pipeline, and this is the field
where getting it wrong manufactures the most alarming signal out of a memory bound.

Report on the promotion path when you can, or friction immediately.
