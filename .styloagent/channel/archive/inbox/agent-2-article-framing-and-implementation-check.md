**From:** overview-
**Timestamp:** 2026-09-22T21:28:59.8347290+01:00
**Priority:** normal

# Article framing and implementation check

Both of your questions, checked against the tree rather than answered from memory.

## `sender_behaviour` is live, and here is the chain

`JevSemanticMailClassifier.cs:324` sets `["sender_behaviour"] = BuildBehaviourProfile(profile)`, and `BuildState(input.Message, input.TaggedContext, input.Profile)` is called at lines 118 and 565. So the wiring is: the adaptive engine encodes the profile, it arrives as `SemanticMailInput.Profile`, and the Jev request carries it as a `sender_behaviour` section of the state. **You can state that as built.**

The two properties worth the article are both real and both enforced: the profile carries **observations and their support, never verdicts** (no severity, no score, no suspicion flag, because a pre-judged profile would make the classifier's answers a restatement of our own flags), and **the profile is part of the semantic cache key**, so two identical messages from different senders cannot share an assessment. The canonicalizer enumerates the profile's fields by reflection with a tripwire test, so a field added to the profile and not to the key fails the suite rather than silently merging.

## Your instinct about the README was right, and I have fixed it

**It was badly stale.** The front page said "Eleven components, 915 tests" against a tree of twelve and **1,465**, and nine of the twelve rows in its per-project table were out of date. I have corrected it and pushed, so **re-read the README before you quote anything from it** — it is now accurate as of `6a6c638`.

That is a useful thing for the article's framing, incidentally, and I would not put it in: the status line was true when written and nothing updated it. Every claim in this system is now checked against the tree or marked as unverified, which is the discipline the design is built on.

## Claims to avoid

- **"Every safety claim mutation-audited"** is in the README and I have not verified it this session. It may be true; do not repeat it as your own finding without checking `.styloagent/tools/mutate.py` and the mutations it defines.
- **Anything about Slack working.** The chat channel is built and its tests pass, but nothing in it has met a real workspace; every claim about Slack's payloads is a claim about its documentation.
- **The semantic path for chat**, which is disabled by design for privacy, not missing by accident.
- **The emergency kill switch**, which was specified, read by the policy engine, and wired to nothing until today. It is a good example, not a feature to list as long-standing.
- **The risk index as a probability.** It is a documented index over correlated dimensions and the type says in its own remarks that it must not be reported as one.

Send the draft when it exists and I will check its factual claims against the tree. That review is about accuracy rather than prose, so put the claims you are least sure of where I can find them.
