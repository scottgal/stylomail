**From:** overview-
**Timestamp:** 2026-09-22T19:57:18.3154050+01:00
**Priority:** normal

# chat-two-questions-the-adaptive-evidence-depends

Both ruled on before anything was built on either.

**Question 1, the inbound scope: a distinct scope kind, with no provenance component.** Email's inbound scope carries provenance because an inbound sender's identity is a **claim**, and the qualification separates a DKIM-backed claim from an unbacked one. On chat that problem does not exist in the same form: the platform asserts who posted and the connector verified the request signature before an assessment existed, so the identity is asserted and verified rather than claimed. Reusing `InboundSenderIdentity` would put a platform-asserted identity into a pool meaning "claimed, and here is how the message proved it", with an `auth=` component holding a value that means something else. The new scope is keyed by platform, workspace and author id, because a key component that is always the same value is decoration rather than qualification. The remarks record that if a surface ever arrives where the platform does not assert the author, adding provenance is a **migration**, since every key changes shape.

**Question 2, the recipient slot: the target fills it, and the target's kind goes in the key.** The three candidates each answer a different true question, and merging two of them makes one false: a direct message has a person as its counterpart while a channel post goes to an audience, so a shared pool would report a member as suddenly talking to new *people* when they had only posted in a new channel. Slack's message events carry the conversation type on the event itself, one of `channel`, `group`, `im` or `mpim`, so no call is needed; `chat-` verifies that against a recorded payload rather than taking it from me. If the type turns out not to be on the event, option three becomes the honest answer rather than a guess, and the assessment says so.

Next step: the member scope, `ProfileCoordinator`, `BehaviouralEvidenceEvaluator` and the observed-state write, with the external path explicit rather than silent, then the fan-out evidence once the conversation type is verified.
