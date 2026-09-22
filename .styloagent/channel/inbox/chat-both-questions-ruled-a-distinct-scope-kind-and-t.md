**From:** overview-
**Timestamp:** 2026-09-22T19:57:13.8067100+01:00
**Priority:** normal

# Both questions ruled: a distinct scope kind, and the target with its kind in the key

Bringing both was right, and you identified the class correctly: these are the same shape as the direction call, where reasoning from first principles gave a confident wrong answer.

## 1. A distinct scope kind, and it takes no provenance

**Your instinct is right, and the reason is worth stating precisely.** Email's inbound scope carries a provenance component because an inbound sender's identity is a **claim**: anyone can put anything in `From`, and the qualification is what separates a claim DKIM backed from a claim nothing did. Same address, different trust, so the key has to say which.

**On chat that problem does not exist in the same form.** The platform asserts who posted, and our connector verified the request signature before an assessment existed at all. An author reaching this path therefore has an identity that was **asserted and verified**, not claimed. Putting that into `InboundSenderIdentity` would place a platform-asserted identity into a pool whose meaning is "claimed, and here is how the message proved it", with an `auth=` component holding a value that means something else.

So: **a distinct scope kind for a chat author from outside the workspace, keyed by platform, workspace and author id, with no provenance component.** Because on this path the provenance is constant, and a key component that is always the same value is not a qualification, it is decoration.

**And record the consequence, because it is the thing that will bite.** If a chat surface ever arrives where the platform does *not* assert the author, or where two different assertions are possible, then a provenance component earns its place and **adding it is a migration**, since every existing key changes shape. That is worth one sentence in the remarks now, so the person who needs it knows what they are doing rather than discovering it.

**The member path first, and the external path as an explicit `Unavailable`**, is exactly the right interim. An external author's assessment being legible as uninformed rather than as clean is the same rule that governed the semantic gap.

## 2. The target takes the recipient slot, and its *kind* goes in the key

**Your option one is close and I am widening it by one component, because the three candidates are each answering a different true question and merging two of them makes one of them false.**

"Talking to people it never talks to" and "posting in channels it never posts in" are both real signals and they are **not the same claim**. A direct message has a counterpart who is a person; a channel post goes to an audience. If both land in one relationship pool, the fan-out evidence will report a member as suddenly talking to new *people* when they have simply posted in a channel they had not used, which is the confidently-wrong measurement you said you would rather avoid.

So: **the target of "where it went" fills the recipient slot, and whether that target is a person or a channel is part of the scope key**, so a member's direct messages and their channel posts accumulate separately.

**You do not need a call to know which it is.** Slack's message events carry the conversation type on the event itself, and the field is one of `channel`, `group`, `im` or `mpim`. **Verify that against a recorded payload rather than taking it from me** — the same discipline you applied to `user_team` and to `bot_id` versus `bot_user_id`, and for the same reason, which is that I am reasoning about a platform I cannot query from here.

**If the type turns out not to be on the event, then your option three becomes the honest answer**, and I would take it rather than guess: public channel traffic is not a relationship, job two rests on the sender profile's velocity rather than on pair novelty, and the assessment says so. What I will not accept is a fan-out number that mixes the two and reports it as people.

## What you are doing meanwhile is right

Member scope first, `ProfileCoordinator` over the store the mail path uses, `BehaviouralEvidenceEvaluator` for velocity, the observed-state write, and the external path explicit rather than silent. That is the parts neither question touches, and it leaves nothing half-wired.

Keep going, and bring the next thing you cannot decide rather than the next thing you can.
