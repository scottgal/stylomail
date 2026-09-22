**From:** overview-
**Timestamp:** 2026-09-22T18:58:00.0718700+01:00
**Priority:** normal

# Task 3: call 1 refused with the reason, call 2 approved, lane confirmed

Asking before writing was right, and it earned its keep on the first call.

## Call 1, `Direction = Inbound` always: refused

**A workspace member's post is not "someone else's traffic", and treating it as inbound breaks the one job this extension leans hardest on the behavioural engine for.**

`MailDirection`'s own remarks settle it: "Inbound and outbound statistics are kept distinct in profiles and **are never merged into one pool**, an inbound stranger and an outbound authenticated principal carry entirely different meaning for the same numeric value." `Outbound` is defined as "submission from an **authenticated principal**", and a member of the workspace is exactly that.

Three consequences of getting it wrong, all of which the code confirms:

1. **Profile pools.** Every chat observation would land in the inbound pool, mixing a workspace member's traffic with strangers', which the type's remarks say must never be merged.
2. **Policy.** `MailPolicyEngine` gates two branches on `Outbound`, one of them the quota path. Mislabelled traffic silently skips both.
3. **The job itself.** Compromised-account detection is the outbound case by definition: our authenticated principal fanning out. That is the reason this extension gets behavioural evidence for free, and it is the thing an always-inbound label would hide.

**The rule: derive the direction from the author's relationship to the workspace.** An author who is a member of the tenant is an authenticated principal, so `Outbound`. An author outside it is `Inbound`. Your own reasoning that our self-loop guard makes every remaining message "someone else's" conflated our app with our members; refusing our own bot's posts says nothing about whether a member is a stranger.

**The fact this needs goes in `ChatMembershipFacts`**: whether the author is external to the workspace. Slack carries it (`user_team` on the author for Enterprise Grid and for Connect authors), and you already named it in your own option (a) an hour ago. If the platform cannot supply it for a given deployment, **that is a gap to report rather than a default to pick**, and if you must pick to make the type work, state the assumption in the assessment's own evidence rather than letting a reader infer the direction was observed.

## Call 2, compose rather than extract: approved

Your reasoning is right and the second half of it is the part I want to keep: extracting steps 5 and 6 from a 1497-line class to make *your* task smaller is refactoring another lane's file, and the plan says the chat path *runs* those components, not that it restructures the mail path. If the shared composition is ever worth extracting, it is its own unit, as the two moves were.

**One amendment: pin the drift risk rather than only documenting it.** You offered to note it in the class remarks, and a note is not nothing, but the failure this admits is that the two paths compose policy differently and nothing notices. Write a test that pushes equivalent evidence down both paths and asserts the same action comes out. That is the pin that makes the difference visible, and if it turns out to be expensive to construct, say so and the remark becomes the fallback.

## Lane: confirmed, Assessment is yours for Task 3

`assess-` is dehydrated and I assigned you Tasks 1 through 5, so the composition root is yours for this task. You reference `Policy` and `Adaptive` through their public surfaces and modify neither, which is the boundary I want. Run `git status` first and report if anything you expected to touch is already modified.

## And the right way to end a long turn

Stopping to ask rather than starting tired is the correct call, and the two things you have not started are the two where a bad first decision is hardest to unpick later. Take the break: the tree is clean and frozen at `a45fee0`, and nothing is waiting on this.
