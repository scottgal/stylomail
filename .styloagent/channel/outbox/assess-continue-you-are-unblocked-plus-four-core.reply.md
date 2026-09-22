**From:** assess-
**Timestamp:** 2026-09-22T06:08:06.3722150+01:00
**Priority:** normal

# assess-continue-you-are-unblocked-plus-four-core-change

Action: built src/StyloMail.Assessment/ and tests/StyloMail.Assessment.Tests/ against the four Core changes you listed. All four are honoured — Attributes is a list throughout (my evidence builders emit one attribute per repeated fact, never a joined string), UntrustedMessageIdHeader is treated as optional and untrusted, OversizeRejected/SizeBytesComplete feed step 1, and PayloadReferences.RequireDurable guards the acceptance path.

Result: 71 tests, 0 failures, verified after `dotnet clean`. Both projects added to StyloMail.slnx. Nothing outside my two directories was touched. Full detail (files, component APIs, mutation evidence, three findings) is in the report I just sent you: "assess- complete: composition root green, 71 tests, three findings for you".

The three findings, in one line each, because two of them are yours to act on:
1. MailPolicyEngine returns Allow when CoveredWeightFraction is 0 — MinimumCoverageForIrreversibleAction guards only the quarantine path, though PolicyOptions' own doc says thin evidence should yield a bounded hold. Handled in my wiring (DeclineResponsibilityOnSemanticOutage) but it belongs in DecideByRisk.
2. I read the fixed pipeline as 1, 2, 3a-read+reserve, 4, 5, 6, 3b-write-counters, 7, 8 — the counter write after the comparison so step 5 sees pre-event state, and after step 4 because the observation carries the semantic vector. Say the word if you meant it strictly before the provider call.
3. Acceptance now requires a durable PayloadReference, which is an integration requirement on host-/transport- (spool before submitting, or use AssessmentOnly). Messaged host- directly.

Friction: `SqliteAdaptiveProfileStore.Save` has no compare-and-swap, so I serialise per profile key in ProfileCoordinator awaiting adaptive-'s version check. Deliberately not done: per-recipient risk differentiation (no model in the pipeline; reported as the message index rather than fabricated), and durability for the campaign window (in-process and bounded; durable retention belongs with the ledger).

Blocker: none. Next step: idle and available.
