**From:** chat-
**Timestamp:** 2026-09-22T19:58:05.0764070+01:00
**Priority:** normal

# chat- Task 3 adaptive increment in: member velocity/drift wired, external author's gap made explicit

The adaptive increment is in, frozen, red-first. The two questions from my last message still stand and still block the rest of it; this is what I could build without guessing at either.

## What is wired

**A member is read through their own profile.** `ProfileKeyHasher.Hash(tenantId, authorId)` gives the pseudonym, `ProfileScopes.OutboundSender` keys the profile, and `BehaviouralEvidenceEvaluator` produces the velocity and drift evidence from it. That is the pool the direction puts them in, and it is the evidence job two rests on: an authenticated principal fanning out.

**No traffic class, so no fan-out question, deliberately.** The mail path only builds a `TrafficClassExpectation` when an operator has declared one, and says why: judging every sender against an expectation nobody named bakes one traffic class's behaviour into every other's. Chat has no declared class either, so I did not invent one, and the fan-out signal is simply not asked for rather than asked badly.

**An external author's missing behavioural evidence is stated, not absent.** The signal ids are present with `Unavailable`, `Value` null, and a new reason code `AssessmentReasonCodes.ChatBehaviouralUnavailable` whose message says the decision was made *without* behavioural evidence rather than with none found. That distinction is the whole point: silence here would read as a quiet stranger.

**Two tests pin the difference**, and the important one is not the one I first wrote. I initially asserted that a stranger's assessment contains no velocity signal, and it failed, correctly: the gap is recorded *as* an unavailable signal rather than as an absent entry, which is the project's rule. So the test now asserts what actually separates the two cases, that the member was read and the stranger was not.

## What remains in Task 3, and what blocks it

- **The fan-out half of the behavioural evidence** waits on question two, whether the channel takes the recipient-and-relationship slot. Without it, job two rests on velocity alone.
- **The external-author scope** waits on question one, what qualifies a chat identity where email uses DKIM and SPF provenance.
- **The observed-state write** is not done. The mail path writes this attempt into observed state at step 3b, after everything it turned out to be is known, counting attempts rather than deliveries. Chat has no acceptance step, so the equivalent line is genuinely different and I would rather settle where it goes alongside question two than place it by analogy.
- **The cross-path drift test** is still not written, and I want to be straight about that rather than let it slide: it is the pin you asked for on the composition duplication, and it is the last thing I will do in Task 3 rather than the first, because it needs both paths complete to compare.

## Measured on the frozen tree

Build 0 warnings, 0 errors. Whole solution **1403 passed, 0 failed, 23 skipped**. Assessment 133 (+2), everything else unchanged. Sweep signals clean.

Continuing: I am taking the observed-state write next, since where the profile is *written* determines what the fan-out evidence can ever be, and answering question two without it would be deciding the read side while leaving the write side unplaced.
