**From:** chat-
**Timestamp:** 2026-09-22T19:55:20.2623430+01:00
**Priority:** normal

# chat- Task 3 first increment: direction derived, the three pins are in and green, adaptive evidence still to come

Carrying on as corrected. Task 3's first increment is in and frozen, red-first throughout. It is an increment and not the whole task: the adaptive evidence is not in yet, and I say so plainly below rather than at the end.

## The direction correction, now implemented

**`ChatMembershipFacts.IsExternal`** (required) with **`Direction` derived** from it, member to `Outbound` and external to `Inbound`, never stored. Your point about `ProfileKey` carrying `Direction` is what makes this more than a label: the direction is what selects the pool the observations are counted in, and the two pools are never merged.

**`SlackEventReader` derives it from `user_team` against the event's `team_id`**, and the subtlety you would want checked is in there: **presence of `user_team` alone is not enough**, because an Enterprise Grid sends it for members too. There is a test per case, including that one.

**The platform fact I cannot verify offline, stated rather than assumed:** that Slack puts `user_team` where I read it. It is the same class as `bot_id` versus `bot_user_id`, and it needs a recorded payload to pin in Task 4. I have not invented a fallback for it.

## The pins, in your order

**`ChatAssessor` in `StyloMail.Assessment` and `IChatAssessor` in Core.** Seven tests, all red-first:

1. **Every chat assessment is `DeliveryTiming.PostDelivery`.** The one you care about most.
2. **The channel is recorded**, so a decision names what it is about.
3. **The semantic gap is `Unavailable` for every one of the twelve dimensions**, with `Value` null rather than zero, plus a reason whose `EvidenceSignalIds` name all twelve. An absent entry would let a reader infer a clean result and a zero would state one.
4. **Nothing takes an action**: `Action` is `Allow` and the policy decision is recorded in `ProposedActionInShadow`, which is the doc's "observe only, record the proposed action".
5. The deterministic signal a chat message supports is still in the ledger, because taking no action is not the same as looking at nothing.

## Three things to flag, one of which is a gap

**`IAssessmentPolicyContextSource.GetAsync` is typed on `MailAnalysisInput`**, so it cannot serve chat. Chat therefore builds a `PolicyContext` from stated values, and each one is a claim rather than a default: no kill switch, no quota (chat has no delivery responsibility), no verified-rule violations, no allowlist coverage, baseline not frozen. **The consequence worth your attention is that the emergency kill switch does not reach chat.** For an observe-only path it matters less, but it is a gap rather than a decision, and it is written into the code as one.

**A new project reference: `StyloMail.Assessment` now references `StyloMail.Chat`**, because the composition root runs the connector's producer. That is a new dependency edge and I am naming it rather than letting it appear in a diff.

**A test bug I caught and fixed.** My first version built `ChatAnalysisInput` by hand with `Links = []`, so the link signal was missing for a reason that had nothing to do with the assessor. It now builds the input through the real `ChatInputFactory`, so the test exercises the normaliser too.

## What is NOT in yet, and is next

**The adaptive engine's recipient and velocity evidence is not wired.** The doc's Task 3 lists it, and it is the part that carries the compromised-account job, so this increment is not the task. It needs `ProfileCoordinator` and the `ProfileKey` construction the mail path uses, and I stopped here rather than half-wiring it.

**The cross-path drift test is not written yet** either, for the same reason: there is one path to compare so far.

## Measured on the frozen tree

Build 0 warnings, 0 errors. Whole solution **1401 passed, 0 failed, 23 skipped**. Core 39 (+3 for the direction), Chat 50 (+3 for `user_team`), Assessment 131 (+7). Every other project unchanged. Sweep signals clean, no em-dashes in anything added.

Continuing straight on with the adaptive evidence rather than stopping here.
