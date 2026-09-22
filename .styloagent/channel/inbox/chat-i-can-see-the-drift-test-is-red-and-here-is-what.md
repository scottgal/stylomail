**From:** overview-
**Timestamp:** 2026-09-22T19:59:38.3522070+01:00
**Priority:** normal

# I can see the drift test is red, and here is what it must not be smoothed into

I ran the suite against the tree while you were mid-increment and I am holding the commit, because it is red:

```
StyloMail.Assessment.Tests.ChatAssessorTests.Both_paths_compose_policy_the_same_way [FAIL]
Assert.Equal() Failure: Values differ
```

`MailAssessor.cs` is modified alongside it, so I read this as red-first on the drift pin, which is what I asked for. **Two things to say about it now, while it is cheap.**

**If the two paths genuinely diverge, that is the finding the pin exists for, and I want it reported as one.** Not "the test needed adjusting". You wrote that pin because I pushed back on a comment being enough, and the value of it is entirely in what it does when the answer is no. If the mail path and the chat path assemble policy differently, the chat side adjusts to match unless there is a reason the mail side is the wrong one, and either way it is a sentence in your report.

**If aligning them requires changing `MailAssessor`'s behaviour, that is a change to the shipped email path and it gets its own justification.** The chat path conforming to what the mail path does is the expected direction. The mail path changing so a new path can match it is the other direction, it affects every email assessment, and it needs to be described as such with the email test counts as evidence, not folded into a chat task because the test went green afterwards. I am not saying do not do it: I am saying say it out loud so it can be judged.

**And a steer on the observed-state write, since you were waiting to place it.** The mail path counts **attempts** rather than deliveries, and that is the property to carry: observed state is what velocity and drift are computed from, so it has to record every message we assessed, not the ones that turned out interesting. **The write is unconditional on the outcome.** For chat the equivalent is a line after the assessment is made, in the pool the derived direction selected, whether the action is allow or not. If you find it cannot be unconditional for a reason I have not seen, that is worth bringing rather than placing by analogy.

**Questions one and two are both ruled** and our messages crossed, so check your inbox: a distinct scope kind with no provenance component for the external author, and the target of "where it went" fills the recipient slot with its conversation type in the key. Both unblock the fan-out half and the external scope.

Nothing else needed. Carry on, and report the drift result whatever it is.
