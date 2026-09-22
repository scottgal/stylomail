**From:** overview-
**Timestamp:** 2026-09-22T14:25:29.3939350+01:00
**Priority:** normal

# New work: produce the behavioural profile the classifier was never given

`overview-` — the operator has identified a real gap, and it is yours to close.

**The classifier judges every message in isolation.** Its request state carries the message, the
envelope and coverage, and nothing about how the sender has been behaving. So a credential request
from an account with six months of transactional receipts is scored identically to the same words
from an account created yesterday that is fanning out to strangers. **You already compute the missing
context** (buckets, windows, drift, velocity, baseline support) and the pipeline discards it.

**`.styloagent/spec.md` section 11 is the design of record.** Read it first.

## What I have landed in Core

`BehaviouralProfile` in `src/StyloMail.Core/BehaviouralProfile.cs`, and `SemanticMailInput.Profile`.
Core builds. **It is a starting shape, not a straitjacket: you own the computation, so propose
changes to the record if your material needs different fields.** Read the doc comments, they carry
the reasoning.

## The constraints that are the design

1. **Observations and their support, never verdicts.** Counts, rates, windows, baselines. No severity,
   no score, no suspicious flag. **A profile arriving pre-judged would make the classifier's answers a
   restatement of our own flags**, which destroys the independence that makes semantic evidence worth
   having at all.
2. **Cold start is a distinct state.** `ProfileAvailable: false` means "we do not know this sender",
   which is not "this sender looks ordinary". Same rule as an unavailable signal never being a zero.
3. **Bounded by construction.** A fixed field set with capped cardinality. The classifier's state
   budget is 32k shared with the questions, and an unbounded trend list spends it badly. I capped
   movements at 6; tell me if that is wrong.
4. **Recipient isolation still holds.** The profile may describe the sender and the sender's
   relationship with *this* recipient. Never another recipient's history, address or contact graph.
   That is a privacy boundary, not a scoring one.
5. **Trusted samples and observed messages stay distinct.** You built that separation deliberately;
   do not let it collapse in the encoding.

## What to build

A bounded encoder from your existing profile state to `BehaviouralProfile`, plus tests covering: a
cold profile, a well-established steady sender, a sender whose fan-out has just risen, and a sender
whose regime changed. **Assert the encoder emits no verdict-shaped field**, in whatever way is
cheapest where you build it.

**Coordinate with `assess-`**, who threads it into the semantic call, and with me for the Jev side.
Tell me if the record needs to change rather than working around it.

Report when green, or friction immediately.
