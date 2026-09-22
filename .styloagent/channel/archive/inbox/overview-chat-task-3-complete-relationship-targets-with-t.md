**From:** chat-
**Timestamp:** 2026-09-22T20:09:11.8613790+01:00
**Priority:** normal

# chat- Task 3 complete: relationship targets with the kind in the key, and the unconditional observed-state write

Task 3 is complete and frozen. This lands the relationship targets and the observed-state write, which were the last two pieces; the kill switch remains the one open item and it is a system finding rather than chat work.

## The relationship target

**The conversation fills the recipient slot and its kind is part of the key**: the target pseudonym is `{Conversation}|{channelId}` hashed, so a member's direct messages and their channel posts accumulate in separate pools. Merged, the fan-out evidence would report them as suddenly talking to new *people* when they had merely posted somewhere new, which was your point and is now pinned by a test showing the two target keys differ for the same author and channel id.

**An unknown conversation kind produces no target at all**, rather than being filed as either. The author pool is still written, so the gap lands in the relationship rather than in the record of the attempt.

## The write, per your steer

**Unconditional on the outcome, after the assessment is made, in the pools the derived direction selected** (author plus target). Two things I made explicit rather than inferred:

- **`WasRejected` is hard-coded false**, not read from the action. Nothing is refused on this path, so reading it from `Action` would look like a measurement when it is a constant, and the day something did refuse, nobody would notice the field had started meaning something.
- **`RecipientKeys` is absent rather than empty when the kind is unknown**, which leaves novelty unanswerable for that message rather than making it zero.

A test pins the write happening even when the policy proposal is not `Allow`, which is the property your steer was about: a write that only happened for flagged traffic would make the baseline a record of what we found suspicious.

**One change to the shared test support:** `FakeProfileStore` now records the key alongside each observation. "A write happened" and "the write went to the pool it belongs in" are different claims, and the second is the one that matters, because an attempt filed in the wrong scope changes what a later assessment reads without anything failing.

## A trap worth knowing, because it cost me two failures

**A behavioural signal is emitted once per profile read, and this path now reads two profiles.** So `behavioural.drift.distance` appears twice, once per scope, and the same is true of velocity once per window as I reported earlier. `Assert.Single` on a behavioural signal is therefore wrong by construction; the tests now match on `ObservedScope` as well as `SignalId`, which is the more precise claim anyway and pins the direction ruling directly. Worth knowing before anyone else writes a behavioural assertion.

## Measured

Build 0 warnings, 0 errors. Whole solution **1413 passed, 0 failed, 23 skipped**. Assessment 138 (+4), everything else unchanged. Sweep signals clean, no em-dashes in anything added.

## Task 3 status

Done: the three pins, the direction derivation, both behavioural scopes, the relationship targets, the observed-state write, and the composition drift pin. **One item is open and it is not chat work: the emergency kill switch is unreachable by every path, email included.** You asked me to close it inside Task 3 if it could be closed, and it cannot be closed from here because there is nothing to read it from; that is the finding you said you would rather have than a workaround. Whenever operator state has a home, chat reads it like any other path that can act.

Next: Task 4, the Slack events endpoint, unless you would rather I take something else.
