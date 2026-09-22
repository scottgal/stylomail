**From:** assess-
**Timestamp:** 2026-09-22T14:34:49.0694590+01:00
**Priority:** normal

# Answer: no, it did not model rate features. Half fixed, half needs your call.

**No. The promotion path did not model `rate.*` at all.** Confirmed, fixed for one of the two paths, and the other half needs a decision from you.

## The answer, precisely

I have two promotion paths and **neither carried rate features**:

1. **Assessment-path rule promotion** (`AssessmentPathLearningRuleId`, default off) promoted `EvidenceVectors.Semantic(...)`, which is `semantic.*` by construction.
2. **Outcome-driven promotion** (`CommitTrustedOutcomeAsync`) promotes whatever `TrustedSample.Dimensions` the caller supplies. Nothing builds such a sample with rates today.

`adaptive-`'s analysis was exactly right, including the part that makes it invisible: the encoder reports the baselines as null and nothing on my side looks wrong.

## Fixed — path 1

It now promotes the window's **`FeatureVector`** (semantic means *plus* both rate features) rather than the message's semantic readings. Two details worth recording:

- **It reads the pre-event snapshot's bucket**, not the post-observation one. Two reasons: the message being judged must not vouch for its own trust, and the window it contributes to should be the history it was measured against rather than one it just moved.
- **It falls back to semantic-only when there is no bucket**, which is the first observation of a profile. That sample genuinely cannot teach a baseline what normal volume looks like, and its *content* says so rather than the code hiding it.

Test asserts `rate.messages_per_second` and `rate.recipients_per_second` are keys in the promoted baseline. **Mutation-verified**: reverting to semantic-only reddens it.

That test also taught me something I would have missed: `FeatureVector` omits the rate features when **zero time has elapsed in the bucket**, because a rate over zero elapsed time is undefined rather than zero. My fixed test clock never advanced, so the sample had no rates and the assertion would have been green over nothing. Real traffic advances the clock; a frozen test clock does not.

## Needs your call — path 2

**`CommitTrustedOutcomeAsync` promotes a caller-supplied vector, and a caller building it from evidence will omit rates exactly as I did.** That is `host-`'s feedback path or whoever wires it, and it is the path that actually runs in production.

I have deliberately not made it silently inject rates into a caller's sample — that would override what the caller said they were teaching, and a baseline moved by values nobody chose is worse than one that is visibly unmodelled. Two shapes I can build, your pick:
- a public helper on `MailAssessor` that builds a correct sample from the stored profile, which callers use instead of assembling their own; or
- leave it, and document that callers supplying their own sample own the rate features too.

**122 green, solution builds my projects.** Note the solution currently fails in `src/StyloMail.Desktop` (`CfDataGetLength`/`CfDataGetBytePtr` unresolved, `MacKeychain.cs`) — not my lane, flagging so it is not attributed to me.

## Your recipient-tracking correction: not applicable, and I checked

You said a null or unknown novelty must not read as "not novel". **Nothing in my pipeline reads novelty.** My observation vector is `EvidenceVectors.Semantic(...)` — `semantic.*` only — and the novelty fields flow from `adaptive-`'s encoder straight into `SemanticMailInput.Profile` without being interpreted. My cache key encodes null distinctly from zero, so an unknown novelty cannot collapse into a reassuring value there either. I am recording it as "checked, not applicable" rather than leaving it as an unexamined assurance.
