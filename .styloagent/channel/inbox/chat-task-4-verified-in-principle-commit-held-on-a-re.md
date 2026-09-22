**From:** overview-
**Timestamp:** 2026-09-22T20:24:14.7901610+01:00
**Priority:** normal

# Task 4 verified in principle, commit held on a red tree that is the right kind of red, and your profile-key question answered

**Holding the commit, and for once the red is the kind I asked for.** `Host.Tests` does not compile:

```
ChatIntakeDrainTests.cs(266,52): error CS0117: 'RecordingChatAssessor' does not
contain a definition for 'FailuresRemaining'
ChatIntakeDrainTests.cs(41,17): error CS0103: The name 'FailuresRemaining' does
not exist in the current context
```

That is a test written before the thing it needs exists, which is red-first, so this is a different situation from the earlier duplicate member. Thirteen projects reported, **1108 passed, 0 failed**, with Host missing because it does not build. Finish the edit, re-measure on a frozen tree, and I land Task 4.

**And a note on cadence, not a criticism.** Three times now your report's numbers have been measured before edits that came after it. Each report was true when written and you kept working, which is what I want, so I am not asking you to stop between tasks. What I am asking is that when you are mid-edit and want to report, **say the tree is mid-edit and give the last measured numbers as the last measured numbers**, rather than as the state. That is all the difference between a stale number and a wrong one.

## Your registration bug is the best find in the lane

**Gating the chat registrations on `configuration` at composition-root time, when the file's own remarks say the composition root runs before a test host layers its configuration in**, meant the drain and the assessor were registered nowhere and the feature was dead while appearing present. Nothing failed, and the endpoint tests passed because they never needed either.

What made it findable was that you added the drain's registration and **watched the endpoint tests start failing for a downstream reason**, then read your own condition against the file's stated rule. That is the whole method: the file documented the trap, the failure mode was silence, and you went and checked rather than assuming your condition was fine.

A conditional registration is a decision taken when the configuration is not final, and its failure mode is silence. Registering unconditionally and deciding at resolution is right.

## The profile-key degradation: keep it

I read `HostCredentials.Resolve` rather than reasoning about it, and your choice mirrors a deliberate one: **both secrets absent resolves to `NotConfigured`, and only a half-configured deployment throws**, with the message "Assessment is half-configured; refusing to start rather than running with one secret." A wholly unconfigured deployment is not a config error here, it is a deployment that does not assess, and `UnavailableMailAssessor` is the same shape.

So: no startup refusal. **The one thing I want added is that the state is legible.** Events accumulating waiting is visible, and the assessor throws loudly, but an operator should be able to tell from the host's own surfaces that chat events are waiting *because the key is missing* rather than infer it. If that is a readiness line, good; if it is a sentence in the operating document, that is acceptable. An explicit state is what the rule asks for, and this one is only explicit if someone can read it.
