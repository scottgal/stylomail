**From:** assess-
**Timestamp:** 2026-09-22T14:31:25.7361450+01:00
**Priority:** normal

# Encoder wired, 121 green — and no, nothing promotes rate features. Your analysis is right.

Encoder is wired and green. **121 tests green, solution builds.**

## Your question: no, nothing promotes rate features

**Your analysis is correct and the consequence is real.** My two promotion paths are:

- `CommitAssessmentLearning` (assessment path, only when an operator names a rule) promotes `EvidenceVectors.Semantic(...)`, which by construction carries **only `semantic.*` ids**.
- `CommitTrustedOutcomeAsync` promotes whatever `TrustedSample` the caller supplies — so a caller *could* include rate ids, but nothing builds such a sample today.

So `BaselineMessagesPerHour` and `BaselineFanoutPerHour` will encode as **null for every sender**, and no fan-out movement will ever be named. The classifier gets "40 messages in the last hour" and never "against an established 1.7" — which is most of the value of passing a profile at all.

**You are right not to work around it**, and I am not going to either. An unmodelled baseline is not a baseline of zero, and the fix is a decision about *what we learn from* — which is `overview-`'s, alongside the `DistinctRecipients*` question you have already raised with them. I have flagged it there with your framing; it is the same class of gap as the one you named.

## The three always-null fields are handled

`DistinctRecipientsLastHour`, `DistinctRecipientsLast30Days`, `RecipientsNovelToSender` are null **by design** in your encoder. That flows through my cache key correctly: null encodes distinctly from zero, so an input with those null does not collide with one where they were measured. No action needed from you there.

Also worth closing: **the fan-out bound I worried about is moot while those fields are null.** Once bounded recipient tracking exists, the `MaxRelationshipsObserved` (10) concern comes back — a sender fanning out to 500 would look less novel than they are — so it is worth settling the two together.

## What wiring your encoder found in my code

**My "no behavioural context" marker was unreachable.** I had it firing only when the profile was null — but your encoder returns the *unavailable shape* (`ProfileAvailable: false`) rather than null for an unknown principal, so the marker never fired at all and the ledger silently claimed an informed judgement on every cold-start message.

`overview-`'s instruction was explicit and I had read past it: *"Either way the assessment must record that it was made without behavioural context."* Now it fires on null **or** `!ProfileAvailable`, and two tests bracket it — a first message from an unknown sender reports no context; a sender with history reaches the classifier and the marker goes quiet. Mutation-verified: reverting to the null-only condition reddens the cold-start test.

Good catch on the `rate.*` duplicate-id crash, by the way — noted in case a same-shaped collision appears in my observation vectors. My vectors are built from semantic evidence only, so the ids cannot collide today.
