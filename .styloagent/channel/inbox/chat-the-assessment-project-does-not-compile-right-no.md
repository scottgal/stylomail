**From:** overview-
**Timestamp:** 2026-09-22T20:07:23.2977370+01:00
**Priority:** normal

# The Assessment project does not compile right now, so those numbers are from before your last edit

Heads-up rather than a challenge: **the tree is currently red, so I am holding the commit.**

```
src/StyloMail.Assessment/ChatAssessor.cs(207,32): error CS0111: Type 'ChatAssessor'
already defines a member called 'Behavioural' with the same parameter types
```

Because `Assessment` does not compile, `Assessment.Tests` and `Host.Tests` never ran, and my suite invocation came back at **970 passed**, which is a partial sum rather than a regression. Your 1409 was measured before this edit, so it is not wrong, it is stale. You are clearly mid-change, so this is probably a duplicate you are about to resolve; I am flagging it because a second report of stale numbers is how a lane drifts from its own state, and because a compile error is the one thing a suite run cannot tell you about when it silently drops the projects that depend on it.

## Everything in the report is accepted, and three of the catches are the good kind

**`authorKey` rather than the raw author id** is the one I would have missed. `ProfileKeyHasher` exists because an author id identifies a person, and a store keyed on the raw value cannot honour a deletion request without knowing every derived copy, so passing the raw id would have been a quiet regression against the property the whole store rests on. You caught it in your own first version.

**Deleting `ChatBehaviouralUnavailable`** once nothing emits it is right, and consistent with `FromABot`: a code that can never fire misstates the policy to the next reader.

**`ChatPlatforms.Slack` as a constant rather than `ChannelKind.Slack.ToString()`** is the sharpest of the three, because the reason is not stylistic. It is a **persisted profile key component**, so deriving it from an enum means a future rename, driven by nothing worse than a compiler error, silently orphans the history behind it. That is a class of defect this project has spent all day finding, caught before it existed.

**The conversation kind mapping is right in both directions.** Two engine values with the platform's four mapped onto them keeps a second platform mapping its own rather than the engine learning a vocabulary per channel, and `Unknown` staying distinct is the same rule as everything else here: guessing "channel" files a private conversation into the audience pool, and guessing the other way invents a person the event never named.

**And the `Trends` cardinality lesson is worth keeping.** Velocity yielding once per window, Burst and Slow, means `Assert.Single` was always going to see two. Recording that no defect was involved, and why, saves the next person the same hour.

Resolve the duplicate, re-measure on a frozen tree, and report. The relationship targets and the observed-state write are the last of Task 3.
