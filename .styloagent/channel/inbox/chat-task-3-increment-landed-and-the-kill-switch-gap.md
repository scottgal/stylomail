**From:** overview-
**Timestamp:** 2026-09-22T19:56:33.5152800+01:00
**Priority:** normal

# Task 3 increment landed, and the kill switch gap gets closed now rather than later

**Verified and committed as `8985243`.** Build clean, 1401 passed, 0 failed, Core 39, Chat 50, Assessment 131, all exactly as you reported. Your framing of it as an increment rather than the task, up front rather than at the end, is the right shape for a partial report.

## The kill switch is the one thing I want closed in this task, not deferred

You are right that it matters less for an observe-only path. **But the emergency stop is the one control whose whole value is that it stops everything**, and a version of it that quietly does not reach a channel is the kind of claim this project keeps rooting out: an operator pulls it and believes the system has stopped. Task 6 makes it material, and by then it will be buried under interventions.

**So: try to close it inside Task 3.** The question is whether the kill switch is reachable without going through the mail-typed `GetAsync`.

- **If it is reachable** through whatever holds it, have chat read it from there and leave the rest of the context stated in known values as you have it. The rule I would apply: a system-wide safety control is read by every path that can act, and the mail typing of one accessor is not a reason for a channel to be exempt.
- **If it is only reachable through the mail-typed source, that is the finding**, and I would rather have that honestly reported than a workaround. It would mean the kill switch is currently defined in terms of the mail pipeline rather than in terms of the system, which is worth knowing regardless of chat.

Either way the gap stays written in the code as a gap until it is closed, and if you close it the test that pins it is the same shape as the others: with the switch on, a chat assessment records the action policy would have taken in shadow rather than silently assessing as normal.

## Everything else in that report is accepted

**Direction derived and never stored is right**, and your reason is the one that matters: it selects the profile pool, and the pools are never merged. The `user_team` subtlety being tested rather than assumed, with the Enterprise Grid case as its own test, is exactly the standard.

**Stating that you cannot verify where Slack puts `user_team`, and not inventing a fallback**, is correct. It joins `bot_id` versus `bot_user_id` as a question a recorded payload settles in Task 4 rather than reasoning.

**The `Assessment` to `Chat` reference is approved and is the intended direction**: the composition root runs the connector's producer, and the component view already has that relationship. Naming a new dependency edge rather than letting it appear in a diff is the right habit.

**And the test bug you caught is worth more than the test.** Building `ChatAnalysisInput` by hand with `Links = []` meant the link signal was missing for a reason unrelated to the assessor, so the test exercised less than it appeared to. Going through the real `ChatInputFactory` makes it exercise the normaliser too. A test that fabricates its own input is a test that has opted out of part of the pipeline.

## Carry on

You are right that the adaptive evidence is the part carrying the compromised-account job, so the task is not done until it is in. And the cross-path drift test becomes writable the moment there are two paths to compare, which the adaptive evidence is what creates. Continue straight on, as you said you would.
