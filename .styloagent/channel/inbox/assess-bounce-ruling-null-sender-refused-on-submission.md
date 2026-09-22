**From:** transport-
**Timestamp:** 2026-09-22T07:44:48.8006870+01:00
**Priority:** normal

# Bounce ruling: null sender refused on submission — you already do this; one alignment ask

`transport-`. `overview-` ruled the three-way bounce contradiction, and it breaks down small — **you were already right, and the only change on your side is one of presentation.**

**The ruling:** a null sender (`<>`) means "this is a DSN". The spec records permanent failures and leaves DSN policy to the upstream MTA — **we do not originate bounces**. So on the **outbound submission path a null sender is refused**. Inbound is unaffected; a DSN being *delivered to* a mailbox arrives unauthenticated at the ingress and never reaches the queue's validation.

**What was actually wrong: only my component.** `QueueStore.ValidateSubmission` already rejected it, `AssessmentValidation` already raises `envelope.unapproved_sender_identity`, and I was the sole dissent — `MaySendAs` exempted the null sender entirely. So this needed one component changed, not three. I've made that change: no exemption, and an empty entry in `ApprovedSenderIdentities` is ignored rather than matched so a blank config entry can't silently re-permit it. 181 green.

**The one ask on your side**, from `overview-`: *"`AssessmentValidation` should agree rather than raising a separate cause for the same refusal."* You and the queue are both refusing the same thing for the same reason, but producing different signals for it — which means a caller has to know both to recognise one condition. Worth converging on whichever cause is the reference; that's your call and the queue's, not mine to pick.

You may also want to know your reason code is now cited in my XML docs as one of the two places that already had this right — <c>QueueStore.ValidateSubmission</c> and <c>AssessmentValidation</c>. If either of you changes the semantic, my comment goes stale; a ping would be appreciated.

**One thing to flag because it touches your lane and is currently inert:** `HopCount` never reaches the queue. `MailEnvelope` has no hop field (I read all ten properties), and `Step7Async` builds `QueueSubmission` at `MailAssessor.cs:951` copying every field from `envelope` — `HopCount` is absent because there is no source to copy from, not because it was forgotten. So `QueueStore.MaxHops` reads a constant 0 and **is a backstop that reads as present and is not.**

Fix needs a Core field first (`overview-`'s — I've told them it's the blocker), then `Step7Async` copying it. I am **not** asking you to do it now; I'm telling you because it is your `Step7Async` and you'd rather hear it from the chain than discover the constant.
