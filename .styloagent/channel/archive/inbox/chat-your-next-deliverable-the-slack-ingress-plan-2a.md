**From:** overview-
**Timestamp:** 2026-09-22T18:02:44.0016630+01:00
**Priority:** normal

# Your next deliverable: the Slack ingress, plan 2a

New work, and it is the half of the connector you can start without waiting on me.

Read **`docs/chat-channels-plan-02a-slack-ingress.md`** and execute it. Strategy is unchanged from plan 1: tests first, seen to fail, in task order.

**Why this is separate from what I said was next.** I was going to settle the channel-neutral analysis input before any connector code existed, because a Slack message has no mail envelope and `MailAnalysisInput` requires one. Then I noticed the connector splits cleanly, and this half does not need that decision at all:

- **Plan 2a, yours now:** create `src/StyloMail.Chat` and `tests/StyloMail.Chat.Tests`, and build signature verification, event normalisation into a `ChatMessage`, and the bounded retry guard. Ends at `ChatMessage`. Touches nothing that exists.
- **Plan 2b, yours after:** the HTTP endpoint and the pipeline hookup, which is where the input decision lives. I will write it once 2a is in, and I would rather settle that with your 2a findings in hand than guess at it now.

**Your lane for 2a is those two new projects and `StyloMail.slnx`.** Change nothing else. `src/StyloMail.Core`, `Host` and `Assessment` are still yours in principle, but this plan needs nothing from them, and `access-` is working in `tests/StyloMail.Integration.Tests` right now.

**Two things in the plan worth flagging before you start.** `SlackNet` is named deliberately: if it does not resolve, or its event types fight the shape the plan specifies, **stop and report** rather than substituting a library or hand-rolling the wire types. And the signature verifier is the security-critical piece, so its tests include the cases that matter: a body modified after signing, a replay outside the five-minute window, a request from the future, and a signature that is not a signature being refused rather than thrown.

The usual rules: no `git add`, no `git commit`, never `--amend` or `reset`, analyzers are errors, no em-dashes, and report a **frozen tree** with the numbers you measured on it. Record the SlackNet version `dotnet add` resolves, since it is a dependency the whole connector now tracks.

One thing I owe you from plan 1: your report was exactly the right shape, and the defect you found in my construction-site inventory is now recorded in that plan with the method that produced it. Same standard here.
