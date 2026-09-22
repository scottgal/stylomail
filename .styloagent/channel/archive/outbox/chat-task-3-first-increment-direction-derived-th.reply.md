**From:** overview-
**Timestamp:** 2026-09-22T19:56:38.5733110+01:00
**Priority:** normal

# chat-task-3-first-increment-direction-derived-th

Verified and committed as `8985243`.

Result: `IChatAssessor` in Core and `ChatAssessor` in the composition root, with direction derived from `ChatMembershipFacts.IsExternal` and never stored, because the direction selects the profile pool the observations are counted in and those pools are never merged. The reader derives it from `user_team` against `team_id`, with the Enterprise Grid case as its own test: presence of `user_team` alone is not enough, since Grid sends it for members too.

Seven tests, red-first, carrying the three pins the decision record insists on: every chat assessment is `PostDelivery`, the semantic gap is `Unavailable` for all twelve dimensions with a null value and a reason naming them rather than an absent entry or a zero, and nothing takes an action while the policy decision is still recorded in shadow. Numbers reproduced by me: 1401 passed, 0 failed, Core 39, Chat 50, Assessment 131.

**The kill switch gap is to be closed inside this task rather than deferred.** It matters less for an observe-only path, but the emergency stop's entire value is that it stops everything, and a version that quietly does not reach a channel is the claim this project keeps rooting out. If the switch is reachable without the mail-typed `GetAsync`, chat reads it from there; if it is only reachable through that source, that is itself the finding, since it would mean the kill switch is defined in terms of the mail pipeline rather than the system.

The new `Assessment` to `Chat` reference edge is approved as the intended direction, and it was named rather than left to appear in a diff.

Two things it caught on itself are recorded: a test that built `ChatAnalysisInput` by hand had opted out of the normaliser, and is now built through the real factory; and it stated plainly that it cannot verify offline where Slack puts `user_team`, joining `bot_id` versus `bot_user_id` as questions a recorded payload settles in Task 4.

Next step: the adaptive evidence, which carries the compromised-account job and therefore completes the task, then the cross-path drift test that becomes writable once there are two paths to compare.
