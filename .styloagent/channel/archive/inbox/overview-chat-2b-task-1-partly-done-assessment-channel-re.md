**From:** chat-
**Timestamp:** 2026-09-22T18:35:36.1563390+01:00
**Priority:** normal

# chat- 2b Task 1 partly done (assessment channel, response, back-fill), and one question I will not guess at

Plan 2b Task 1 is half done, tested first and frozen. The half I have done is the assessment-contract and response half; the `ChatAnalysisInput` half needs one decision from you, set out at the end.

## What is done

**`MailAssessment.Channel` is required.** Tests first: a reflection test alongside the two plan 1 added, failing red as CS0117 before the property existed.

**The second back-fill is in the converter**, exactly as the design you approved it. Both entries now sit under one comment stating why they are back-fills rather than defaults, in the code rather than in a message: `MailAssessor` is the only production construction site and it is the email path, so a row written before chat existed was an email assessment made while still in the delivery path.

**`DecisionResponse` now carries both `channel` and `deliveryTiming`**, so the sentence "the console shows `deliveryTiming` on every chat decision" is finally satisfiable, and it was unsatisfiable on every decision, chat or email.

**One deliberate deviation worth your eye.** The design says the email call sites state `ChannelContext.Email`. I did that for the test fake, but in `MailAssessor` I wrote `Channel = analysis.Channel` instead of the literal. The input already declares its channel, so carrying it through records the channel the message actually arrived on rather than asserting email from the one place that currently only ever sees email. Same result today, and it is not a lie waiting to happen if the assessor is ever reached by another channel. Say the word if you would rather have the literal.

**Call sites:** exactly two existed, `MailAssessor.cs` and `Host.Tests/TestSupport.cs`, which the compiler confirmed by building clean after both.

**Measured on the frozen tree:** build 0 warnings, 0 errors. `dotnet test StyloMail.slnx`: **1365 passed, 0 failed, 23 skipped.** Core 32 (+1), Host 305 (+2, the legacy-row and console-view tests), and **Assessment unchanged at 124**, which is the evidence the assessor edit changed no behaviour. Every other project's count is unchanged from item 2; the AccessProxy, Desktop and Integration numbers that differ from this morning are `access-` and `desktop-` adding tests concurrently.

The legacy test now strips **both** members from a row this build actually wrote, and asserts `Email`, `PreAcceptance` and three nulls, so it pins the whole back-filled shape rather than one field.

## The question: what are "the platform's bounded membership facts"?

Task 1 says `ChatAnalysisInput` carries "the platform's bounded membership facts". I am not going to invent those, because the Core side is exactly where plan 2a's design said not to design the Slack membership model from. Here is what I can see, and the proposal.

**What plan 2a actually establishes today.** `ChatMessage` carries `AuthorId`, `WorkspaceId`, `ChannelId`, `ThreadId` and the event id. That is the whole of what the connector currently knows about who the author is.

**Two shapes are defensible and I do not think it is mine to pick:**
- **(a) A small record of named, platform-asserted facts**, for example the author id, whether the platform marked the account a bot or app, and whether the author is external to the workspace (Slack carries `user_team` on the event for Enterprise Grid authors, which is exactly this fact). Bounded by construction, because the field list is fixed. This is my recommendation.
- **(b) The `TaggedContext` precedent**, a bounded map of platform-asserted key/values. I would avoid it: a map with a stated bound is a map that grows the first time a connector wants to add a key, and "bounded" then rests on discipline rather than on the shape.

**And a finding that bears on the choice.** **Plan 2a drops bot messages entirely** (`SlackEventIgnored.FromABot`, and it is tested). So if we define an `IsBot` membership fact, nothing can ever reach an assessment carrying it, and the field is dead on arrival. That is a connector decision rather than a Core one, and it has two honest resolutions: either the connector records the fact and lets the assessment see it as context, or the field does not exist and bot traffic stays out of the system entirely. **Which of those you want changes Task 4 as well as Task 1**, which is why I am raising it now rather than after both are written.

**What I will do on your word:** with (a) I will add `ChatAnalysisInput` and its tests immediately, and Task 4 populates the facts from the event payload. With the bot question answered as "keep dropping them", I will leave the field out rather than ship a fact that is structurally always false.

## What this leaves open for Tasks 2 and 3

`ChatAnalysisInput` is the input Tasks 2 and 3 both consume, so I have not started them. The rest of Task 1, the evidence producer, and the assessment path are all downstream of this one answer, and everything above is committed-ready on its own if you want to land it separately.
