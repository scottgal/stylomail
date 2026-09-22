**From:** chat-
**Timestamp:** 2026-09-22T20:06:41.7891620+01:00
**Priority:** normal

# chat- both scope rulings implemented: conversation kind carried, distinct author scope wired, external gap closed

Both rulings are implemented and green. This is the fan-out read side and the external scope; the observed-state write and the relationship targets are the last piece and are next.

## Ruling 1: a distinct author scope, no provenance

**`ProfileScopeKind.ChatAuthor`**, with `ProfileScopes.ChatAuthor(tenantId, platform, workspaceId, authorKey)`. The remarks carry your reasoning rather than a summary of it: that an email sender's identity is a claim and the qualification separates a DKIM-backed claim from an unbacked one, that a chat author's identity is asserted by the platform and verified by the connector before an assessment exists, and that placing it in `InboundSenderIdentity` would put it in a pool whose `auth=` component means something else.

**And the consequence you asked to be recorded:** there is no provenance component because on this path the qualification is constant, and a key component that is always the same value is decoration; if a surface ever arrives where the platform does not assert the author, adding one is a migration and existing keys change shape. That is in the enum's remarks where the next person will read it.

**One correction I made to my own first version:** the factory takes `authorKey`, not the platform's author id, and requires it to be a pseudonym. `ProfileKeyHasher` exists because an author id identifies a person, and a store keyed on the raw value cannot honour a deletion request without knowing every derived copy. Passing the raw id would have been a quiet regression against a property the whole store rests on.

**The external path is now wired**, and `AssessmentReasonCodes.ChatBehaviouralUnavailable` is deleted because nothing emits it any more. Same reasoning as `FromABot`: a reason code that can never fire misstates the policy to the next reader.

## Ruling 2: the conversation kind, carried and mapped

**`SlackConversationType`** on the message, read from the event's `channel_type`, with `Unknown` as a distinct state rather than a default: guessing "channel" would file a private conversation into the audience pool and guessing the other way would invent a person the event never named.

**`ChatConversationKind` in Core is the engine's two**, `Audience` and `People`, and the connector maps its four onto them. I did that rather than putting the platform's four into the engine so a second platform maps its own onto the same two rather than the engine learning a vocabulary per channel. **`Unknown` maps to `Unknown`**, not to `Audience`, for the reason above.

**Unverified offline, and flagged in the code:** that the platform reports the type on a message event at all, and that `channel_type` is the field. Same discipline as `user_team` and the bot identifiers, and it needs a recorded payload in Task 4.

**`ChatPlatforms.Slack` is a constant, not `ChannelKind.Slack.ToString()`**, because it is a persisted profile key component: a key containing "slack" addresses history written under that value, and deriving it from an enum means someone reading a compiler error renames it and orphans the history behind it.

## A test failure I chased that was mine, not a defect

A stranger's assessment appeared to have no velocity signal while a member's did, from identical empty profiles, which would have been a real asymmetry. It was my test: **`Trends` yields velocity once per window**, Burst and Slow, so `Assert.Single` saw two matches and reported failure. `Assert.Contains` passes. No defect, and worth knowing before anyone else pins a trend signal by cardinality.

## Measured

Build 0 warnings, 0 errors. Whole solution **1409 passed, 0 failed, 23 skipped**. Chat 55 (+5), Core 39, Assessment 134, everything else unchanged. Sweep signals clean.

## Next, and the last of Task 3

**The relationship targets and the observed-state write.** The conversation kind is now on the input ready for the key, the write is unconditional after the assessment in the derived pool as you steered, and what remains is placing both once the target keys exist. That is what I am doing next.
