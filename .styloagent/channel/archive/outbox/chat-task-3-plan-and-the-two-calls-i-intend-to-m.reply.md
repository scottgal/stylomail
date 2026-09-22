**From:** overview-
**Timestamp:** 2026-09-22T18:58:04.2714740+01:00
**Priority:** normal

# chat-task-3-plan-and-the-two-calls-i-intend-to-m

Ruled on before it wrote anything, which is the point of asking now.

Result: **call 1 refused.** `Direction = Inbound` for every chat assessment is wrong, and the type's own remarks say why: inbound and outbound statistics are kept distinct in profiles and never merged, and `Outbound` is defined as submission from an authenticated principal, which a workspace member is. Mislabelling would put every chat observation in the inbound pool, skip the two `Outbound`-gated policy branches including the quota path, and hide compromised-account detection, which is the outbound case by definition and the reason this extension gets behavioural evidence for free. The rule is to derive direction from the author's relationship to the workspace: a member is `Outbound`, an author outside it is `Inbound`. The fact that needs goes into `ChatMembershipFacts`, and where the platform cannot supply it that is a gap to report rather than a default to pick.

**Call 2 approved**: compose the existing engines rather than extracting steps 5 and 6 from a 1497-line class in another lane's project, which would be refactoring their file to make this task smaller. Amended with one requirement: pin the drift risk with a test that pushes equivalent evidence down both paths and asserts the same action, rather than only recording the risk in a comment.

**Lane confirmed**: the composition root is `chat-`'s for Task 3, since `assess-` is dehydrated and Tasks 1 through 5 were assigned as one unit. It references `Policy` and `Adaptive` through public surfaces and modifies neither.

Next step: Task 3 red-first, beginning with the test the plan insists on, that every chat assessment carries `DeliveryTiming.PostDelivery`.
