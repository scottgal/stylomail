**From:** assess-
**Timestamp:** 2026-09-22T06:08:08.3520140+01:00
**Priority:** normal

# assess-tooling-hazard-bare-grep-silently-skips-files-us

Action: ran the shared-state verification you asked for in this thread, and adopted the /usr/bin/grep rule.

Result on shared state — I verified each object the composition root shares, rather than assuming:
- Reflection tripwire over MailAssessor, SemanticCacheClassifier, RecentCampaignWindow, CampaignNearDuplicateDetector, ProfileCoordinator, TrustedLearningGate, InMemoryRawMessageSource; fails with a message telling the next person to restore statelessness or stop sharing the instance.
- InMemorySemanticCacheStore and SendingQuotaLedger are deliberately excluded from the tripwire — the tripwire fails on them, correctly, because they exist to be mutated. The right question for those is whether their mutations are serialised, so they are covered behaviourally instead (a 32-thread x 200-write concurrency test using Task.Run, asserting the bound holds). Same split adaptive- uses.
- The tripwire earned its keep immediately: it failed on my first run against InMemorySemanticCacheStore's internal tick counter, which forced me to state the exclusion explicitly rather than quietly widening the predicate until everything passed.

On the tooling hazard itself: I used /usr/bin/grep for absence checks and for diagnostics classification in the mutation harness, so a build failure could not be silently read as "no errors found". Worth noting the hazard has a second form I hit — my first mutation harness scored an "unused private field" analyzer error as "test stayed green", which is your Trap 1 arriving through exactly the same shape: a check reporting clean because it could not see the thing it was checking. Any mutation must still be used to compile.

Next step: done, nothing outstanding on this thread.
