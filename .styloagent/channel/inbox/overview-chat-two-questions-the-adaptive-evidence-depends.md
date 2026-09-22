**From:** chat-
**Timestamp:** 2026-09-22T19:56:04.3178380+01:00
**Priority:** normal

# chat- two questions the adaptive evidence depends on, with evidence, before I guess at either

Continuing on Task 3's adaptive evidence, and I have hit two things I will not invent. Both are the same class as the direction call, which I got wrong by reasoning from first principles, so I am bringing them rather than deciding them.

## 1. The inbound sender scope needs email's authentication provenance, and chat has none

`ProfileScopes.InboundSender(tenantId, senderKey, AuthenticationContext authentication)` builds its key as `{senderKey}|auth={provenance}`, where provenance comes from an `AuthenticationContext`, which is DKIM and SPF results. **A chat message has no `AuthenticationContext` and no equivalent.**

`ProfileScopes.OutboundSender(tenantId, senderKey)` takes no authentication, so **the member case is buildable today**. The external-author case is where it stops.

This is the design's own point arriving as a type error: the design says Slack authenticates the member and not the message. So the chat analogue of "how was this identity authenticated" is not DKIM at all, it is what the platform asserted about the account, and that qualification decides the profile key and therefore the pool an external author's history accumulates in.

**What I need:** what qualifies a chat identity for the inbound scope. My instinct, offered as a starting point rather than a proposal, is that the qualification should be the deployment's own assertion that the platform authenticated the account, with no notion of message provenance at all, and that this is a different scope kind from `InboundSenderIdentity` rather than a reuse of it. But that is a vocabulary decision and it is yours.

**Meanwhile:** I can wire the member path now, and I will emit the external path's behavioural evidence as an explicit `Unavailable` with the reason rather than leaving it silently absent. That is the honest pattern and it keeps an external author's assessment legible as uninformed rather than clean.

## 2. Does the channel take the recipient-and-relationship slot?

Email's fan-out evidence is per `Relationship` scope, a sender-recipient pair, built from the envelope's `RcptTo`. **A chat message has no recipient list.** The channel it was posted in is the only "where it went" the event carries.

That matters because "is this member suddenly talking to people it never talks to" is job two, and you named it as the reason this extension gets behavioural evidence for free. If the channel is the wrong key, that job is not covered by chat at all, and I would rather know now than build fan-out detection that measures the wrong thing confidently.

**Three candidates, and I do not have a principled favourite:**
- **The channel** takes `Recipient`, and `Relationship` becomes author-and-channel. Simple, available from every event, and makes "posting to channels it never posts to" expressible. My slight preference, because it is the only one the event always carries.
- **The counterpart in a direct message** takes it, with channel posts having no relationship at all. Truer to "talking to people", but the event does not distinguish a DM from a channel without another call, and I do not want to make one per message.
- **Public channel traffic is not a relationship at all**, and chat's fan-out comes from a different signal entirely. Possibly the most honest, and it would mean job two rests on the sender profile's velocity rather than on pair novelty.

## What I am doing while you decide

Wiring the parts neither question touches: the direction-selected author scope for members, `ProfileCoordinator` over the same store the mail path uses, `BehaviouralEvidenceEvaluator` for velocity, and the observed-state write. The external path gets the explicit `Unavailable` rather than silence. The channel-as-recipient piece waits on question two.

Nothing is committed, the tree is green at 1401 passed, and Task 3's first increment is frozen as reported.
