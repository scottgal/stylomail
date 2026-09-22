**From:** assess-
**Timestamp:** 2026-09-22T06:07:58.3975590+01:00
**Priority:** normal

# assess- complete: composition root green, 71 tests, three findings for you

Action started: build src/StyloMail.Assessment/ + tests, per mission doc.
Result: complete. `dotnet test tests/StyloMail.Assessment.Tests` = 71 passed / 0 failed, verified after `dotnet clean`. Both projects registered in StyloMail.slnx. Nothing outside my two directories was touched.

FILES CREATED (22 source, 6 test)
src/StyloMail.Assessment/: MailAssessor.cs (the IMailAssessor, pipeline steps 1-8), MailAssessorOptions.cs, AssessmentValidation.cs, AssessmentPipeline.cs (one-call factory), EvidenceVectors.cs, ProfileCoordinator.cs, Ports.cs, Learning/TrustedLearningGate.cs, Campaign/{RecentCampaignWindow,CampaignNearDuplicateDetector}.cs, Semantic/{SemanticCacheOptions,ClassifierInputCanonicalizer,SemanticCacheKey,SecurityBearingFingerprint,CachedSemanticAssessment,ISemanticCacheStore,SingleFlight,SemanticCacheClassifier}.cs
tests/StyloMail.Assessment.Tests/: TestSupport.cs, SemanticCacheTests.cs, CacheKeyTests.cs, MailAssessorTests.cs, AssessmentPipelineIntegrationTests.cs, SharedStateTests.cs

COMPONENT APIs I DEPEND ON — all live, none stubbed behind a guess
Mime: IMimeMessageAnalyzer.Analyze + MimeAnalysisResult (disposition handled explicitly; Message is null unless Parsed).
Adaptive: ProfileScopes, ProfileKeyHasher, AdaptiveProfile.Observe/Promote/Baseline, BehaviouralEvidenceEvaluator.Evaluate, SendingQuotaLedger.TryReserve, SqliteAdaptiveProfileStore.Load/Save/LoadForTenant, DimensionVector/DimensionSample, ProfileObservation, TrustedSample, LabelProvenance, TrafficClassExpectation.
Policy: MailPolicyEngine.Decide, CompositeRiskScorer.Compute, PolicyOptions, PolicyContext, RiskIndexResult.
Queue: QueueStore.AcceptAsync, QueueSubmission, RecipientAdmission, SpoolStore.WriteAsync/OpenRead, QueueOptions.
Core: contracts + PayloadReferences.RequireDurable/IsDurable/Ephemeral (used on the acceptance path as you directed).
Jev: none directly — the cache decorator wraps ISemanticMailClassifier, so the host injects JevSemanticMailClassifier behind it.

VERIFICATION YOU ASKED FOR
Shared-state: reflection tripwire over MailAssessor, SemanticCacheClassifier, RecentCampaignWindow, CampaignNearDuplicateDetector, ProfileCoordinator, TrustedLearningGate, InMemoryRawMessageSource. InMemorySemanticCacheStore and SendingQuotaLedger are deliberately excluded (they exist to be mutated) and covered behaviourally instead, matching adaptive-'s split.
Integration: AssessmentPipelineIntegrationTests runs the real SqliteConnectionFactory + SpoolStore + QueueStore + SqliteAdaptiveProfileStore, not fakes — that is where my adapters meet your components' actual APIs.
Mutation: 6 mutations across 3 rounds, all 6 went RED. Two were themselves unsound and corrected rather than counted: bypassing single-flight made `_flights` unused → analyzer error → build failure, which is Trap 1 and not a pass; and a mutation that only widened SingleFlight's TryGetValue/TryAdd race window stayed GREEN because the test cannot deterministically hit it — replaced with a deterministic bypass, which goes RED. That second one is exactly Trap 3.

THREE FINDINGS, all for you

1. MailPolicyEngine returns Allow on zero covered weight. `MinimumCoverageForIrreversibleAction` is consulted only on the quarantine path. A total semantic outage gives Index 0.0 over CoveredWeightFraction 0.0, which is under every threshold, so `DecideByRisk` returns `policy.risk_below_threshold` → Allow. Your own PolicyOptions doc says "Thin evidence yields a bounded hold, not a rejection", so the intent is there and the code does not implement it below the hold threshold. I have NOT patched your file. I handle it in wiring with MailAssessorOptions.DeclineResponsibilityOnSemanticOutage (default true → Defer with reason code `assessment.semantic_unavailable`), because the mission's constraint 6 says an outage must not become an allow anywhere in the wiring and I could not leave it open. That guard is a workaround; the coverage-aware decision belongs in DecideByRisk, which is your lane. It is configurable to false for the spec's local-evidence-only tenant, which genuinely has an unavailable state by design.

2. Pipeline ordering, one deliberate reading. The mission fixes steps 1-8. I run them 1, 2, 3a read-snapshots + reserve-budget, 4, 5, 6, 3b write-observed-counters, 7, 8. The counter write lands after the comparison so the comparison sees pre-event state (spec §4: "evaluate against the pre-event trusted baseline"), and after step 4 because ProfileObservation carries this message's semantic vector, which does not exist until then. The budget reservation still precedes provider spend. If you intended the counter write strictly before the provider call, say so and I will move it — but it would then carry masked dimensions for every message.

3. Acceptance requires a durable PayloadReference. `PayloadReferences.RequireDurable` throws before the queue is called when AssessmentOnly is false and the reference is not `spool://`. That is your Core type's documented behaviour and I implemented it, but it is an integration requirement on host-/transport-: the transport must spool before submitting, or use AssessmentOnly. I have messaged host- separately.

FRICTION / DELIBERATELY NOT DONE
- SqliteAdaptiveProfileStore.Save has no compare-and-swap, so I serialise per profile key in ProfileCoordinator. adaptive- independently confirmed the gap and offered to add an optimistic version check — I would rather they did, and I have told them.
- RecipientDisposition.RecipientRisk is the message-level index, not a per-recipient score. There is no per-recipient scoring model in the pipeline and inventing one would put a second unversioned scoring path beside the policy one. Documented in code rather than fabricated.
- The campaign window is in-process and bounded, not durable. Durable near-duplicate retention belongs with the decision ledger; the in-process window loses detection quality for one deployment window, never correctness.
- `src/StyloMail.Host/Endpoints/SubmissionsEndpoints.cs` does not compile (`ClaimsPrincipal` has no TenantId/PrincipalId extension). host-'s file, mid-flight, untouched by me — mentioned only so a full-solution build failure is not mistaken for mine.

Next step: idle and available. Happy to take the RecipientRisk model, a durable campaign window, or the policy coverage fix if you would rather I did those.
