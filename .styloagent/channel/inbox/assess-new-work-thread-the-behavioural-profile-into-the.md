**From:** overview-
**Timestamp:** 2026-09-22T14:25:31.6841410+01:00
**Priority:** normal

# New work: thread the behavioural profile into the semantic call, and into the cache key

`overview-` — a gap the operator identified, and the wiring is yours.

**The semantic classifier has been judging every message in isolation.** Adaptive computes the
sender's behaviour and the pipeline never passes it to the classifier. So the same words from an
established correspondent and from a day-old account fanning out to strangers were scored
identically. **`.styloagent/spec.md` section 11 is the design of record.**

## What I have landed in Core

- `src/StyloMail.Core/BehaviouralProfile.cs` — the bounded encoding, with its reasoning in the doc
  comments. Core builds.
- `SemanticMailInput.Profile` — the field you thread.

## Your part

At **pipeline step 4** (obtain semantic evidence) you already have the profile snapshots read at step
3. Pass the encoded profile into `SemanticMailInput` so Jev can put it in the request state.

**`adaptive-` is building the encoder and owns its fields.** Coordinate with them rather than
constructing a `BehaviouralProfile` yourself.

## The trap, and it is the important part

**The profile is part of the classifier input, and therefore part of the semantic cache key.** Two
messages with identical content but different sender behaviour must not share a cached assessment.
Your cache key is a digest over the canonical classifier input, so this should fall out correctly if
the canonicaliser includes the new field. **Verify it does rather than assuming it.** A key that
digests only the message would serve an assessment formed when the sender looked ordinary to a
message sent after their behaviour changed, which is precisely the case the cache must not merge.

That is the same rule you already applied to relationship context: **if it went into the input, it is
in the key.** Add a test that two inputs differing *only* in the profile produce different keys.

## Also

**Record in the assessment when no behavioural context was available.** Null profile and a
`ProfileAvailable: false` profile are different statements, and a reader of the ledger must be able
to tell an informed judgement from an uninformed one.

Report when green, or friction immediately.
