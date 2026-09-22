**From:** chat-
**Timestamp:** 2026-09-22T18:57:29.7447130+01:00
**Priority:** normal

# chat- Task 3 plan and the two calls I intend to make, before I write anything

I have done the reconnaissance for Task 3 and I am about to start it red-first. Two calls in it are mine to make but cheap for you to correct, and one is a lane question. Sending them before the code because correcting me now costs a message and correcting me after costs a rewrite.

## What `MailAssessor` actually depends on

Ten collaborators, and the useful part is which of them chat has a counterpart for:

**Reusable as they stand:** `ProfileCoordinator` over `IAdaptiveProfileStore` (profile snapshots), `CompositeRiskScorer`, `MailPolicyEngine` with `PolicyInput`, `IAssessmentPolicyContextSource`, `TrustedLearningGate`, `MailAssessorOptions.Policy`.

**No counterpart, and correctly so:** `IMimeMessageAnalyzer` (chat's evidence comes from `ChatEvidenceProducer`), `IContextualSemanticClassifier` (chat is local-only), `IMessageAcceptanceQueue` (no delivery responsibility), `IRawMessageSource` (no payload to re-read), `SendingQuotaLedger` (it budgets *outbound* recipients, and our own posts are dropped before they reach here).

**Deliberately out of scope:** the recent-campaign window, which is triage's near-duplicate check and therefore plan 3.

## The two calls I intend to make

**1. `Direction = MailDirection.Inbound` for every chat assessment.** `PolicyInput` requires a direction, and chat has no envelope to derive one from. The honest answer is that every message reaching this path is someone else's traffic, because our own app's posts are refused in the reader and cannot arrive. If a later chat channel posts on our behalf for some other reason, that reasoning breaks, so I would rather state it now than have it inferred from a default.

**2. Compose the existing engines rather than extracting `MailAssessor`'s steps.** Steps 5 and 6 are inline in a 1497-line class, and extracting them would mean refactoring `assess-`'s file to make my task smaller. The doc's Task 3 says the chat path *runs* the producer, the adaptive evidence and policy, which reads as composing them rather than restructuring the mail path. So the chat assessor calls `CompositeRiskScorer` and `MailPolicyEngine` directly. The cost is that the two paths could drift in how they compose policy, and I will say so in the class remarks rather than leave it implicit. **If you would rather I extracted the shared composition, that is a bigger change to `assess-`'s project and I would want it as its own unit, like the two moves.**

## The lane question

Task 3 is "the chat assessment path in the composition root", so it lands in `src/StyloMail.Assessment`, which is `assess-`'s project. You have assigned me Tasks 1 through 5, so I am reading that as mine for this task, but I am not going to start editing their project on an inference. **Confirm and I start immediately.** It references `StyloMail.Policy` and `StyloMail.Adaptive` through their public surfaces and modifies neither.

## What I will pin, in the order the doc insists on

Red-first, and the first test I write is the one you said matters most: **every chat assessment carries `DeliveryTiming.PostDelivery`**. Then the semantic gap as an explicit `Unavailable` with a reason rather than an absent entry or a zero, and then that nothing takes an action.

I have not started editing. Everything up to and including the builder move is committed and frozen, and this turn has run long enough that I would rather start Task 3 clean than start it tired.
