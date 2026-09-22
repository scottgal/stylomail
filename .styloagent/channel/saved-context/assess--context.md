# `assess-`, saved context

## Identity and scope

Composition root for StyloMail. Owns `src/StyloMail.Assessment/` and
`tests/StyloMail.Assessment.Tests/`. Wires MIME + semantic classifier (+ cache decorator) +
adaptive + policy + queue into the single `IMailAssessor` the host consumes. **Do not edit any
other project**, message the owner instead.

## Repo state

- Repo: `/Users/scottgalloway/RiderProjects/stylomail`, branch `main`, **no commits yet** (the whole
  tree is untracked). Do not run `git add`/`git commit`.
- Build: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`.
  SDK 10.0.201, `net10.0`.
- Both my projects are registered in `StyloMail.slnx`.

## Fleet

`overview-` (root) · `mime-` · `adaptive-` · `queue-` · `host-` · `assess-` (me) ·
`transport-` (SMTP/MTA transport, queue delivery port, Cloudflare connector) ·
`access-` (IMAP/POP3/SMTP access proxy, state `needs you`).

## Deliverable, DONE

`dotnet test tests/StyloMail.Assessment.Tests` → **117 passed, 0 failed** on **per-run verified-clean
trees** (both sweep signals checked before each run). `dotnet build StyloMail.slnx` → **succeeds**.

**Verify per run, not per session.** My last verification loop: 3 runs clean/0 failed, and **5 runs
SKIPPED because a sweep lock appeared mid-loop**, the convention working exactly as intended. An
earlier single run went red (3/110) and I had checked the tree *afterwards*, not before, so I could
not attribute it; the immediately following runs were green. **Same mistake `access-` made; the fix is
a per-run check, which is now what I do.**

(Count went 104 → 101 because the gate/stripes/retry-loop tests were deleted along with the
machinery they tested, see decision 8. Fewer tests over code that no longer exists is the correct
direction, not a regression.)

**Report ALL THREE when claiming done.** Two fleet rules, both from someone else's pain:

1. **`dotnet build StyloMail.slnx`** (`overview-`, after `queue-` broke `transport-`'s build without
   noticing). Your own project green is not the claim "nothing I did broke anyone".
2. **`dotnet test StyloMail.slnx`** (`access-`, refining the first): build catches compile breaks and
   **cannot** catch a suite that passes one run in four, and that failure mode is worse, because it
   *looks* green when checked. "Build succeeds" and "tests pass" are different claims.

Build your own project to avoid being blocked by another lane's transient red; build *and test* the
solution before declaring completion. **And attribute any non-mine failure rather than claiming a
clean sweep**, a gate that depends on another lane's flakiness is not usable as a personal claim, so
name what failed and whose it is.

Files created:

```
src/StyloMail.Assessment/
  MailAssessor.cs                     the IMailAssessor; pipeline steps 1-8
  MailAssessorOptions.cs              limits, version stamps, learning rule, outage guard
  AssessmentValidation.cs             step 1 rules + AssessmentRules ids
  AssessmentPipeline.cs               one-call factory over the shared SQLite + spool
  EvidenceVectors.cs                  semantic evidence -> DimensionVector (masked, never zero)
  ProfileCoordinator.cs               the named place where "append, or decide?" is answered;
                                      two store operations, two counters
  Ports.cs                            IRawMessageSource, IAdaptiveProfileStore,
                                      IMessageAcceptanceQueue, IAssessmentPolicyContextSource
  Learning/TrustedLearningGate.cs     the only door to trusted learning
  Campaign/RecentCampaignWindow.cs    bounded, per-tenant, holds vectors + digests only
  Campaign/CampaignNearDuplicateDetector.cs
  Semantic/SemanticCacheOptions.cs
  Semantic/ClassifierInputCanonicalizer.cs   length-prefixed canonical form + digest
  Semantic/SemanticCacheKey.cs
  Semantic/SecurityBearingFingerprint.cs     + PaymentIdentifiers
  Semantic/CachedSemanticAssessment.cs       + SemanticCoverage
  Semantic/ISemanticCacheStore.cs            + InMemorySemanticCacheStore (LRU/LFU, expiry)
  Semantic/SingleFlight.cs
  Semantic/SemanticCacheClassifier.cs        the decorator + IContextualSemanticClassifier
tests/StyloMail.Assessment.Tests/
  TestSupport.cs, SemanticCacheTests.cs, CacheKeyTests.cs, MailAssessorTests.cs,
  ProfileCoordinatorTests.cs, AssessmentPipelineIntegrationTests.cs, SharedStateTests.cs
```

## Component APIs depended on (all live, none stubbed)

| Component | What I use |
| --- | --- |
| Mime | `IMimeMessageAnalyzer.Analyze(MimeAnalysisRequest) → MimeAnalysisResult`; `Message` null unless `Disposition == Parsed` |
| Adaptive | `ProfileScopes.*`, `ProfileKeyHasher.Hash`, `AdaptiveProfile.Observe/Promote/Baseline`, `BehaviouralEvidenceEvaluator.Evaluate`, `SendingQuotaLedger.TryReserve`, `SqliteAdaptiveProfileStore.Load/Save/LoadForTenant`, `DimensionVector/DimensionSample`, `ProfileObservation`, `TrustedSample`, `LabelProvenance`, `TrafficClassExpectation` |
| Policy | `MailPolicyEngine.Decide(PolicyInput)`, `CompositeRiskScorer.Compute`, `PolicyOptions`, `PolicyContext`, `RiskIndexResult` |
| Queue | `QueueStore.AcceptAsync`, `QueueSubmission`, `RecipientAdmission`, `SpoolStore.WriteAsync/OpenRead`, `QueueOptions` |
| Core | all contracts; `PayloadReferences.RequireDurable/IsDurable/Ephemeral` |
| Jev | none directly, the decorator wraps `ISemanticMailClassifier`, so the caller injects `JevSemanticMailClassifier` |

## Decisions a fresh me must not re-litigate

1. **Pipeline order is 1, 2, 3a (read snapshots + reserve budget), 4, 5, 6, 3b (write observed
   counters), 7, 8.** The counter write lands *after* the comparison so the comparison sees
   pre-event state, and *after* step 4 because the observation carries this message's semantic
   vector. The budget reservation still precedes provider spend.
2. **Accepted `EvidenceAvailability.Unavailable` is never memoised.** The semantic cache stores
   nothing when the provider answered nothing.
3. **`ServeStaleOnProviderUnavailable` defaults to false.** Off, an outage stays an outage.
4. **`DeclineResponsibilityOnSemanticOutage` defaults to true.** A total semantic outage would
   otherwise be an Allow on zero covered weight, see the issue filed against `MailPolicyEngine`.
5. **Campaign near-duplicates are recorded for assessment-only calls too.** What assessment-only
   excludes is the observed-rate counters and the recipient budget (things that gate delivery), not
   the detection window (which gates nothing and holds no assessments).
6. **Assessment-only is kept out of acceptance by an explicit guard, not by `RequireDurable`.**
   There is a test with a *durable* reference and real bytes, because with an ephemeral one
   `RequireDurable` would refuse it anyway and the guard would never be shown to be load-bearing.
7. **The assessor is the only component that accepts.** `overview-` ruled on this after `host-`
   found that both of us called `AcceptAsync` under different keys, two acceptances the queue could
   not dedupe, masked only because `host-`'s `spool://pending` resolved to nothing. `host-` deletes
   its `AcceptAsync`. **Do not stop accepting.**
8. **The queue's `IdempotencyKey` is the caller's, passed through, never a minted one.**
   `context.ClientIdempotencyKey`, null when absent. My original `assessmentId` was a genuine bug:
   it is minted fresh per attempt, so it silently replaced the client's replay contract with one we
   could not keep, and it made `host-`'s replay fast-path dead code whose tests certified it as
   working. **No fallback key ever**, inventing one looks like replay protection while providing
   none.
9. **`MailAssessment.SubmissionId`** is non-null exactly when a durable queue row exists. Built via
   `AcceptanceOutcome.Accepted/Refused` so acceptance and decline cannot both be reported. It is
   also set on a *duplicate* replay, carrying the **existing** item's id, non-null does not mean
   "we created something".
   Because the id is identical in both cases it cannot answer that question, so Core gained
   **`MailAssessment.Submission`** (`SubmissionAdmission?`, `Created` / `Duplicate`), nullable and
   **null exactly when `SubmissionId` is null**. Set it on every acceptance path; never infer it.
   `overview-` promoted it from reason codes to a Core field after I raised the caveat, reasons are
   explanations, and pulling a fact out of prose to choose a status code is stringly-typed coupling.
   `assessment.submission.created` is **gone**; `assessment.submission.duplicate` stays as
   **explanation only** and must not be read as the fact. Agreement between the two fields is
   structural in `AcceptanceOutcome.Accepted` and asserted from outside by
   `TheSubmissionAdmissionAlwaysAgreesWithTheSubmissionId`.
10. **Acceptance requires a durable `PayloadReference`.** A non-durable reference on the submission
   path throws via `PayloadReferences.RequireDurable`. The transport must spool for real or use
   `AssessmentOnly`. A reference that is durable *in form* but names no stored payload gets a
   distinct refusal naming the reference, that case cost `host-` an hour because it looked exactly
   like a storage fault.
8. **Profiles have exactly two operations, and neither is optimistic.** They are the store's, not
   mine, do not reintroduce a save/retry layer.
   - **`ApplyObservation(key, observation, at)`**, a pure append. The burst path: many messages for
     one sender arrive at once.
   - **`Update<T>(key, at, profile => ...)`**, runs a *decision* inside the same write lock. The
     rare path: promotions, learning commits.
   Both take the write lock **before** reading, so many writers queue rather than race, and neither
   needs a retry. **The split is about shape, not safety**, what `ApplyObservation` buys is that
   "this is an append" is stated once, in the store, rather than every caller reimplementing the
   merge inside an update delegate.

   **Deleted entirely:** a striped array of 256 per-profile gates, a bounded compare-and-swap retry
   loop, `ProfileUpdateConflictException`, and `ProfileWriteStatistics.SaveAttempts`/`Conflicts`.
   The retry loop could not survive a burst, measured at 4 attempts and 4 conflicts against
   sustained ingest, and the gate only ever covered writers inside one process, which the store
   already handles. It was deleted rather than tuned.
   `ProfileWriteStatistics` now counts `Observed` and `Mutated`; **`Mutated` climbing at message
   rates is the regression signal** (whole-profile writes drifting onto the ingest path).
   There is no whole-profile `Save` on the port at all, nothing called it.

## Thread-safety status of what I hold

- `MailAssessor`, no reassignable instance state; asserted by
  `SharedStateTests.SharedTypesCarryNoReassignableInstanceState`.
- `InMemorySemanticCacheStore` / `SendingQuotaLedger`, deliberately excluded from the tripwire (they
  exist to be mutated); covered behaviourally by `TheCacheStoreIsSafeUnderConcurrentWriters`.
- `RecentCampaignWindow`, `SingleFlight`, `ProfileCoordinator`, cache store, all internally locked
  or concurrent.
- The gates are waited on with `WaitAsync`, not `Wait`, the critical section contains a SQLite
  write, and blocking a thread-pool thread across it is how a busy host starves itself. This was
  fixed when the coordinator became async; the earlier blocking version is gone.

## Mutation evidence

Thirty-three mutations across eleven rounds, all thirty-three RED after corrections, after one
correction that mattered: the LFU mutation stayed GREEN on the first attempt, which is how the
toothless LFU test was found. Round 11 swept declared-but-unexercised surface. Round 10 covered
the two-path split after adopting `Update`: routing observations through the whole-profile update
reddens the unit test; counting mutations as observations reddens another. Round 9 was the budget. Round 9 covered the budget
reserve/release pairing: never released; released even when dispatched; released without ever having
reserved (quota can never bind). One mutation, over-releasing to test the shortfall counter, stayed
GREEN, and the reason is worth keeping: it changed the *argument* to `Release` while the comparison
stayed against `reservation.Recipients`, so it never reached the check. The counter was left provable
only by the dependency characterisation test above. Round 8 proved
observations really take the delta path: reverting `Observe` to load-observe-Save reddens both the
conflict-rigged unit test and the burst against the real store. Round 7 covered the
Core `Submission` field: never reported; reported while the id is not (the drift case); always
Created; always Duplicate; and a refused acceptance still claiming an admission, that last one
would have been missed without writing it, because `Refused` builds from a different factory and the
agreement held by accident of construction until mutated. Round 5 covered the
submission seam: reverting to the minted key (unit *and* against the real `QueueStore`), never
reporting a submission id, and fabricating one. The fabricated-id mutation came back **green**
first, the test asserted the id was non-null, which a fabricated value satisfies, so the fake now
records the ids it returns and the test asserts against those. Round 4's mutation is the one
that mattered most: removing the per-profile gate left the burst test **GREEN in isolation** and red
only under full-suite load, a regression test that catches a bug only sometimes is barely better
than none. Adding `Statistics.Conflicts` and asserting `SaveAttempts == total` and `Conflicts == 0`
made it deterministic, and the same mutation now goes red immediately.

Three mutations were themselves inconclusive/weak and were corrected rather than counted:

- `_flights` becoming unused after bypassing it → analyzer error → build failure, **not** a pass.
- Ignoring `SingleFlight.TryAdd`'s result *stayed green*, because the mutation only widens a
  race window the test cannot deterministically hit. Replaced with a deterministic bypass, which
  goes RED.

All three are the fleet's Trap 1/Trap 3. A test that cannot fail is not a test.

## Rolled the ledger's window (adaptive-, 2026-09-22)

`SendingQuotaLedger` now takes `(int recipientsPerWindow, TimeSpan window, TimeProvider)`, a
**rolling window**, so the budget is a rate limit rather than a lifetime cap. It broke my build
(one-arg constructor) and **only `dotnet build StyloMail.slnx` surfaced it**; my own suite stayed
green throughout.

Two consequences to keep straight:
1. **My earlier "permanent deferral" claim is narrowed.** The reserve-without-release fix still
   stands, budget burned by refused traffic makes a legitimate sender hit its rate limit sooner than
   its allowance should, since the quota bounds *dispatched* volume, but it is no longer
   rescue-from-a-permanent-cap. Do not restate the stronger version.
2. **DONE, the budget clock is per call, not per ledger.** `SendingQuotaLedger` takes
   `(int recipientsPerWindow, TimeSpan window)` and `DateTimeOffset at` on
   `TryReserve`/`Release`/`Remaining`. **`MailAssessorOptions.TimeProvider` is deleted**, it existed
   only to feed their constructor, and with the timestamp passed in there is no clock to be wrong
   about. Reserve and release pass the **same `now`**, so elapsed time between them is exactly zero
   and no reservation can age out of the window between them.
3. `BudgetReleaseShortfall` has a second, benign cause: a reservation that *expires* between reserve
   and release looks short. Needs a window shorter than one assessment, so impossible at the 1h
   default, documented so the next person checks the window before assuming divergence. With
   per-call timestamps the elapsed time is exactly zero, so this becomes unreachable rather than
   merely unlikely.
4. **`quotaExhausted` is a per-request local, never cached.** Verified by grep, not recalled.
   `IAssessmentPolicyContextSource.OutboundQuotaExhausted` is caller-supplied and *can* be cached by
   a caller, which with a window means reporting stale exhaustion for up to an hour; documented on
   the port to read it live, and noted that leaving it false is safe because the assessor derives
   exhaustion from its own reservation attempt.

## FIXED: a promotion racing a burst used to be lost

`adaptive-` built the store-side `Update<T>` callback I asked for. A promotion now runs inside the
same `BEGIN IMMEDIATE` transaction as the ingest merge, so it cannot conflict with a burst at all.

`APromotionSurvivesSustainedIngestOnTheSameProfile` **replaced its own inverse**, the previous
version asserted `ProfileUpdateConflictException`, characterising the limitation; it now asserts the
promotion lands, with `BurstInterposingProfileStore` forcing the race deterministically rather than
hoping for it. The first version of that test ran a real background burst and was **flaky** (passed
three runs, then a probe caught 4 attempts / 4 conflicts).

## The outbound recipient budget, reserve AND release

`SendingQuotaLedger` has **no rolling window**: once spent, spent for the life of the process. So
reserving on every outbound assessment without ever releasing meant a legitimate principal reached
its ceiling once and was **deferred permanently**, a self-inflicted outage where every message the
quota blocked was itself the reason it stayed blocked.

Rule: release **only** when (a) the reservation actually succeeded **and** (b) acceptance did not
happen. Both halves matter.
- Releasing an *unsuccessful* reservation returns budget that was never taken, and because
  exhaustion is what produced the deferral, the quota would un-exhaust itself on every message and
  **never bind at all**.
- Releasing an *accepted* one hands back budget for recipients about to be delivered.

`MailAssessorStatistics` exposes `BudgetReservations`, `BudgetReleases`, `BudgetReleaseShortfall`
(should be zero; non-zero means our tally and the ledger's diverged and the outer figure is no
longer trustworthy). `AShortfallIsDetectableAtAll` is a **characterisation test of the dependency**
, if `Release` ever returns the amount requested instead of the amount given back, the counter
becomes an inert line reading zero and nothing else in the suite would notice.

## MUTATION SWEEPS BREAK OTHER LANES' BUILDS AND TESTS

**`.styloagent/tools/mutate.py` rewrites source files IN PLACE in the shared working tree**, read,
`.bak`, `write_text`, no isolation. While a sweep runs, any lane that references the swept project
gets failures that are real, reproducible and caused by a file on disk.

**Before believing ANY failure, check:**
```
ls .styloagent/tools/.mutation-sweep.lock      # running sweep
find src -name '*.bak'                          # SIGKILLed sweep residue
```
Both absent ⇒ tree clean ⇒ the failure is real. **Do not "fix" code that was never wrong**, that is
the real cost, not the wasted debugging.

**RULES FOR MY OWN SWEEPS (I broke both):**
1. **Take `.styloagent/tools/.mutation-sweep.lock`** for the duration. My rounds never did.
2. Backups in `/tmp`, **never `.bak` in-tree**, I got this right by accident, which is why no residue
   was ever found (and why nobody could tell a sweep was running).
3. Remove the lock and verify no residue before declaring the sweep finished.

**My own harnesses must adopt the lock.** They mutate `src/StyloMail.Assessment/*` via a `/tmp`
backup (never `.bak` in-tree), which is why no residue was ever found, but they never took the
lock either, so another lane could have been testing through my mutations.

**I WAS A SECOND SOURCE AND DID NOT ENUMERATE MYSELF.** `StyloMail.Host` references
`StyloMail.Assessment` **since 06:57:56**. My rounds 12/13/14 ran at **07:01 and 07:09**, mutating
Assessment in place, no lock, against a project Host now referenced, minutes before the 07:16 failure
window. Not established that they caused anything (my last sweep ended ~07:12); established that I was
an enumerable candidate and the only person who could see it.

**The reflexive half of the clause, which is the part I missed:**
> **Enumerate your own lane as a possible cause for others, not only other lanes as a possible cause
> for you.**

I checked whose tooling could explain my failures. I never checked whether mine could explain theirs.
My harnesses were `/tmp/assess-mutation-round*.sh`, **not in `.styloagent/tools/`, so an audit of
sweep tools would not have found them.** Disclosed to `overview-`, and **deleted**, so the
un-auditable artifact no longer exists to be re-run unsafely. `overview-` has made the reflexive
clause fleet policy and asked `queue-` for a *convention* (any sweep, anywhere, takes the lock) plus
making the lock discoverable in `PROTOCOL.md` rather than only in a directory you must know about.

**If I sweep again: rewrite the harness under those rules**, lock, `/tmp` backups, lock removed
before declaring done.

**This explained four "flakes" attributed to the wrong lanes:**
- `StyloMail.Queue` "flaky 9-in-10" → the sweep mutating Queue's own source.
- `StyloMail.Host` "~20% flaky" → **Host references Queue**. Most Host failures were in tests calling
  `QueueStore` directly (`CountByStateAsync`) or the delivery worker. **One was not**,   `AssessmentTests.A_body_naming_the_callers_own_tenant_is_accepted` names no Queue type at all
  (corrected by `access-`); it builds a full `TestHost`, which composes Queue, so the mutation reaches
  it transitively through host construction. **The mechanism holds; my statement of it did not.**
  Confirmed: 20 clean runs on per-run-verified-clean trees across two agents.
- 34 "distinct tests across 8 classes" failing → a varying victim set is the *signature* of a moving
  mutation, not of shared state.

## Favourable evidence is the kind that does not get checked

`host-` corroborated the sweep window with `mutate.py`'s mtime of 07:24 and wrote "your window holds".
**It is weaker than that.** A tool file's mtime records when the tool was *written*, not when a sweep
*ran*, a running sweep rewrites `src/StyloMail.Queue/*.cs`, never `mutate.py`. The timestamps
(`mutate.py` 07:24, `mutations/` dir 07:13, `queue.py` 06:53, `mime.py` 06:57) show a
**tooling-authorship window**, consistent with sweeps running but not a run log, and none exists.

The causation stands without it: Host references Queue, every failing test in the thread calls
`QueueStore` or the delivery worker, and the in-place mutator is independently verified.

**I flagged it because it supported a conclusion I already held.** That is the fifth member of the
four-claims pattern and the only one that would have made the record *stronger* rather than weaker, so it is the one nobody checks. Same discipline as narrowing the budget claim, pointing the other way.

## Flake experiment recipe (for whoever picks up `host-`)

`host-` ran out of budget with an open, **not-root-caused** flake in `StyloMail.Host.Tests` and asked
for this experiment. I ran it; here is the recipe so it survives.

**Do NOT add `xunit.runner.json` to another lane's project.** xUnit reads the same settings from a
runsettings file, so pass it instead, same experiment, no files touched:

```xml
<!-- /tmp/serial.runsettings -->
<RunSettings><xUnit>
  <ParallelizeTestCollections>false</ParallelizeTestCollections>
  <ParallelizeAssembly>false</ParallelizeAssembly>
</xUnit></RunSettings>
```
`dotnet test <proj> --settings /tmp/serial.runsettings`

**Always two controls:**
1. **Manipulation check**, serial runs must be measurably slower (here 5s → 15s). Without it, a
   setting that silently did not apply makes "no flake" meaningless.
2. **Interleave the arms** (parallel, serial, parallel, …) rather than blocking them. A blocked design
   lets a tree edit land entirely in one arm, the mistake that produced my "deterministic" call.

**Result as of 2026-09-22 07:30: 0 failures in 10 runs per arm, INCONCLUSIVE, not negative.** Zero
events means nothing to compare; a 1-in-10 rate would need ~100 runs per arm. `Queue` was 88/88 green
at the same time, so `host-`'s cross-suite-interference hypothesis is untestable now (no aggressor).

## Re-measure before attributing a cause, and control the tree

**The rule, from `access-`, adopted:** "Flaky" and "deterministic" are both claims about a
*distribution*, and a single run, or three runs inside one edit window, cannot distinguish them.
**Re-measure before attributing a cause.**

**Extended after running `host-`'s experiment:** eight agents editing one tree means neither reading
is reliable **unless the tree is controlled**. Four wrong claims came out of this one thread, my
"deterministic" (3 runs), `access-`'s "cleared" (1 run), `access-`'s "9-in-10 flaky" (measured mid-
refactor), my "88/88 green" (1 run, a real outlier). All in good faith, all arithmetically right,
all wrong about the system. **A flake report needs the tree state it was measured on and a rate
measured on a quiesced tree, or the next person re-derives a transient and calls it a defect.**

Worked example, which is the whole point:

| Repetitions | Conclusion | Who |
| --- | --- | --- |
| 3 back-to-back | "deterministic, same 3 tests" | me, **wrong** |
| 1 run | "cleared" | `access-`, **wrong** |
| 10 runs | "flaky, 2/10, varying victims" | the answer |

`StyloMail.Host.Tests` is **flaky at roughly 20%, with differing victims**, the three tests I named
have not failed since, and the one failure I caught was a test I had never seen fail. A false
"deterministic" is worse than a false "flaky": it tells someone the problem will still be there when
they return. **I handed `host-` a work item based on an inference the evidence could not support**,
and had to retract it urgently.

**Why 3 runs is not enough:** three back-to-back runs take ~30 seconds. In a tree being actively
edited, thirty seconds of stability is stability of a **tree state**, not of a defect. Repetition
under identical conditions measures that nobody saved a file recently.

`StyloMail.Queue.Tests`: `access-` sampled 10× → **9 failures**, 34 distinct tests across 8 classes
(up to 27/88 in one run). My single green run was real and unrepresentative. That breadth is shared
state, not a race between two tests.

## The null sender, three crashes found by writing one test (round 12)

`transport-` asked me to align a reason code for null-sender refusal. Writing the counterpart test, *an inbound DSN is legitimate mail*, failed three times in a row, each further along:

1. **`SenderKey`** → `ProfileKeyHasher.Hash(tenant, "")` **throws**. Every inbound DSN crashed the
   assessor at profile-key derivation.
2. Fixing that moved the crash to **`Campaign()`**, which hashes `MailFrom` for the sender scope.
3. Then I grepped all `ProfileKeyHasher.Hash` sites rather than letting tests walk me through them.
   There were exactly three, and there is now **one `NullSender(...)` helper** used by all of them.

**Why a helper and not a third fix:** the failure is a *throw*, not a wrong answer, so a missed call
site is a crash on legitimate mail, and I missed one. A fourth cannot now be added quietly.

**The alignment answer:** my old rule caught a null sender only as a *side effect* of an identity
mismatch, so it fired only when `ApprovedSenderIdentities` was non-empty. With no list configured it
passed straight to `QueueStore.Require(MailFrom)`, which **throws**, so **whether a message crashed
or was cleanly declined depended on unrelated configuration.** Now an explicit, unconditional,
direction-aware rule: `envelope.null_sender_not_permitted`, **outbound only**.

**RESOLVED by `queue-`:** inbound null sender is **representable and accepted** (a DSN to one of our
users is ordinary mail); outbound returns `Refused(RefusedNullSender)` rather than throwing. Their
underlying defect was worse than the null-sender check, `Require(MailFrom)` threw *before*
`IsNullSender` was consulted, so inbound `""` was refused unconditionally while a comment claimed
inbound was unaffected. Fixed on their side.

**And their contract detail found a divergence in MY rule.** Mine was `IsNullOrWhiteSpace`, catching
`""` and **missing `"<>"`**, so `MailFrom = ""` was refused before provider spend while `MailFrom =
"<>"` ran the full classification and was refused later by the queue. **Same input, two outcomes,
decided by notation**, the exact "a rule that only sometimes fires is not a rule" failure the rule's
own docs warn about. Now mirrors `QueueStore.IsNullSender` exactly, with four forms under a
`[Theory]` (`""`, whitespace, `<>`, `< >`) each asserting refusal **before** the classifier is called.

**Known duplication:** two definitions of the same predicate in two assemblies, mine citing theirs as
the reference. Asked `queue-` to ping if theirs changes; suggested Core if `overview-` wants one.

**`HopCount`, FIXED, and the fix taught the sharpest lesson of the session.** `overview-` added
`int? HopCount` to `MailEnvelope`, `queue-` made `QueueSubmission.HopCount` nullable and the guard
`is { } hops && hops >= MaxHops` (null = not observed, guard silent). My line: **copy it faithfully
including null**, collapsing null to 0 would turn "we did not check" into "there were no hops".

Three tests, the third being the one that matters: against the **real `QueueStore`**, a message at
exactly `MaxHops` is refused and surfaces as a `Defer` with `assessment.acceptance_refused`. It could
not pass before, because the queue compared a permanent default of 0.

**The chain is COMPLETE, verified link by link (not taken from anyone's status):**
```
SmtpIngressSession.cs:599            HopCount = facts.ReceivedCount
CloudflareEmailRoutingConnector:331  HopCount = facts.ReceivedCount
HostIngressSink.cs:130               HopCount = submission.HopCount -> MailEnvelope
MailAssessor.Step7Async              HopCount = envelope.HopCount   -> QueueSubmission
QueueStore.cs:124                    is { } hops && hops >= MaxHops -> guard fires
```
`transport-` believed the sink link was pending; it was not. **My test only proves the chain *given*
the envelope carries a count**, if nothing populated the field, my tests would pass while the
backstop stayed inert in production. Checked rather than accepted either account: same
declared-but-unexercised shape, one layer up.

**THE LESSON, I reported a prose change I had not made.** I told `transport-` I had "documented it at
the construction site"; `HopCount` appeared *nowhere* in my source. My edit helper replaced a string
and printed "ok" **unconditionally**, the anchor didn't match, so it no-op'd and told me it worked.

**Why only this one slipped: every behavioural change had a test, and a silent no-op fails a test.
This was the only purely-prose change, and nothing verifies prose.** So the one class of change with
no verification was the one I reported as done. **Use `Edit` (errors on a non-matching anchor) for
one-off edits, and grep for the thing you claim to have written**, a mutation harness asserts its
anchor, but an ad-hoc edit helper must too, or it lies.

## Behavioural profile threaded into the semantic call (overview-, 2026-09-22)

**DONE. 121 tests green, solution builds.** `BehaviouralProfileEncoder.Encode(snapshot, now)` is
called at step 4 from the **sender** snapshot; the result goes into `SemanticMailInput.Profile`.

**The cache-key trap was live and is fixed.** `SemanticMailInput.Profile` landed in Core and *nothing
included it in the canonicaliser*, so the key digested only the message — two messages with identical
content and different sender behaviour would have shared one cached assessment. All **18**
`BehaviouralProfile` fields plus 3 on `DimensionMovement` are encoded, with **null distinct from
zero**. Tests: differing only in `MessagesObserved` → different keys; **null vs `ProfileAvailable:
false` → different keys**; a **field-count tripwire** so a Core field added and not encoded fails the
build with "encode it". Mutation-verified.

**My "no behavioural context" marker was UNREACHABLE, found by writing its test.** It fired only on a
null profile, but the encoder returns the *unavailable shape* (`ProfileAvailable: false`) for an
unknown principal — so the ledger silently claimed an informed judgement on every cold-start message.
`overview-`'s instruction was explicit and I had read past it: *"either way the assessment must record
that it was made without behavioural context."* Now fires on null **or** `!ProfileAvailable`.
Mutation-verified.

**ANSWERED AND HALF-FIXED — `rate.*` features were never promoted.** `overview-` asked directly; the
answer is **no, on both paths**. Fixed **path 1** (assessment-path rule promotion): it now promotes the
window's **`FeatureVector`** (semantic means + both rate features) from the **pre-event** snapshot's
bucket, falling back to semantic-only for a first observation. Mutation-verified.

**Path 2 — `BuildTrustedSample` helper BUILT per `overview-`'s ruling** ("documenting a pitfall is not
the same as removing it"). Two overloads: one taking `(tenantId, direction, senderIdentity, at,
provenance, label)` which derives the pseudonym itself, and one taking a resolved `ProfileKey`.
**Outbound only** — an inbound sender key is qualified by authentication provenance, so a caller cannot
name it from the identity alone; that overload throws rather than guessing a key and teaching the wrong
profile. Path 1 now promotes through the same `LearningVector`, so **one definition** serves both.

**The key-derivation pitfall was one layer below the rate-feature one, and my first test hit it:** I
assembled a `ProfileKey` by hand from the raw address, named a profile that did not exist, and got a
dimension-less sample that taught nothing — silently. Same shape, same fix: do not make the caller know
an internal detail.

**THE FIELD-COUNT TRIPWIRE FIRED, within the hour.** `adaptive-` added
`RecipientDistinctnessIsFloor` (bool) to `BehaviouralProfile`; my tripwire failed with "declares 19
properties but EncodeProfile encodes 18". Encoded it — **a truncated count and an exact one are
different observations even when the number matches**, so the flag is as load-bearing as the value.
That tripwire is the single best-value thing I wrote today.

*(Original note preserved below.)*
**Path 2 (`CommitTrustedOutcomeAsync`) was open with `overview-`**: it promotes a
caller-supplied vector, so a caller assembling one from evidence omits rates exactly as I did — and
that is the path that runs in production. I have **not** made it inject rates silently (that would
override what the caller said they were teaching). Two shapes offered; awaiting their pick.

Test lesson: `FeatureVector` omits rate features when **zero time has elapsed in the bucket** (a rate
over zero elapsed time is undefined, not zero), so a frozen test clock produces a rate-less sample and
a green assertion over nothing. The test now advances the clock.

*(Original note preserved below.)*
My two promotion paths carry `semantic.*` only, so `BaselineMessagesPerHour` / `BaselineFanoutPerHour`
encode as **null for every sender** and no fan-out movement is ever named. The classifier gets "40
messages this hour" and never "against an established 1.7" — most of the value of passing a profile.
`adaptive-` deliberately did not work around it; an unmodelled baseline is not a baseline of zero.
Fix is a decision about *what we learn from*: `overview-`'s, alongside the `DistinctRecipients*`
question.

**Also open:** `DistinctRecipientsLastHour/30Days` and `RecipientsNovelToSender` are null by design
(adaptive- keeps no recipient set on the sender). Handled correctly by the key. **When bounded
recipient tracking lands, my `MaxRelationshipsObserved` (10) bound returns as a correctness issue** —
a sender fanning out to 500 would look less novel than they are. Settle the two together.

## Declared-but-unexercised surface sweep (round 11)

Applying the "an interface member with no callers reads as a supported path" discipline across my
own surface found three, plus a toothless test:

1. **`RecipientDisposition.RecipientScopedSignalIds` was never populated.** Core declares it, this
   pipeline is its only producer, and it was always null, reading as an honest "nothing
   recipient-specific here" on every message. Now attributed: `ProfileTarget` carries the recipient,
   `Behavioural` returns per-recipient signal ids alongside the flat evidence, and dispositions carry
   them. **Null still means something specific**, a recipient past `MaxRelationshipsObserved` has no
   pair profile, and there is a test for each case.
2. **`SemanticCacheEvictionPolicy.LeastFrequentlyUsed` was untested**, and the branch had been wrong
   once already. Now tested, and the FIRST version of that test was **toothless**: degrading LFU to
   LRU left it green, because the key I chose as least-frequent was also least-recent. Rewritten so
   the two policies disagree (a most-read, oldest entry), and mutation-verified.
3. **`MaxLinks`, `MaxAttachments`, `MaxBodyCharacters` had no test.** Three mandatory limits whose
   removal reddened nothing. Now one `[Theory]` case each.

## A counter nobody can read is not instrumentation

Found by applying `adaptive-`'s zero-callers discipline to my own code, one message after they
applied it to theirs: `ProfileWriteStatistics` lived on a **private** `ProfileCoordinator`, so no
operator surface could see it. The counter I had called "the one assertion I'd keep" had no
production reader, the same defect as `Release` having no production caller.

Now exposed as **`MailAssessor.ProfileWrites`**, with its limits documented honestly: it records
which operation the coordinator *chose*, not which store method it reached, so it catches "learning
committed at message rates" (a real operational signal) but **not** a misimplemented `Observe`. The
store-level assertion in `ProfileCoordinatorTests` is the stronger check, do not read a clean
`ProfileWrites` as proof the split is intact.

## Deleting a mechanism invalidates the reasoning that chose it

Found three stale justifications in my own code in one session, all the same shape: a comment
explaining a decision by a constraint that a deletion removed. Nothing fails when this happens, so
the reasoning outlives the mechanism indefinitely.

- The delta-path justification ("a burst on the whole-profile path would be many writers racing one
  row"), false once `Update` also took the write lock before reading. Split is about **shape**, not
  safety.
- "Authorise before touching the store" justified by "every other writer would then lose a
  compare-and-swap", there is no compare-and-swap any more. Still right, for `adaptive-`'s reason:
  the update always writes and holds the database's single writer.
- `ProfileCoordinator`'s doc describing the gate/retry it no longer has.

**Rule: when deleting a mechanism, grep for the comments that justified it.** A comment claiming a
constraint that no longer holds is worse than no comment, the next person may keep a costly design,
or delete a safe one, on its authority.

## transport- seam: retired

The transport has **no spool at all**, `IngressSubmission.RawMessage` is bytes, never a reference,
and they refuse to resolve `spool://` anywhere (verified by their own grep). So there is only one
spool in the system, the queue's, and my `SpoolRawMessageSource` looks in the only place it could.
The obligation is entirely `host-`'s sink: spool into **the same `SpoolStore` instance the
composition root passes to `AssessmentPipeline.Create(...)`**, and **before** `AssessAsync`.

`AssessmentOnly` is **never** right for an ingress that answers `250`, a `250` after `DATA`
transfers delivery responsibility, so accepting under that flag means claiming the message with no
queue row behind it. The flag is for a caller wanting a verdict without handing over the message.

## Fake-fidelity trap found the hard way

`FakeProfileStore.Load` returns the stored instance **by reference**, where the real
`SqliteAdaptiveProfileStore` reconstructs from the database on every load. So with the fake, a retry
that reloads sees its own previous attempt's mutation rather than the untouched stored row, and a
`Promote` in the retry compounded to a baseline version of 2. **Anything that depends on
reload-after-conflict semantics belongs in `AssessmentPipelineIntegrationTests` against the real
store.** The fake may prove the retry *loop* runs (attempt counts, bounded budget); it may not prove
what a retry observes. Documented on `Load` in the fake.

## Known trades in my lane

- `UpdateObservedStateAsync` writes each profile sequentially, so a message with ten relationship
  profiles costs ten SQLite round trips. Bounded by `MaxRelationshipsObserved`; parallelism would
  help latency but risks exhausting the SQLite writer. Deliberately sequential.
- `MailAssessor.CommitTrustedOutcomeAsync(request, ct)` replaced the sync `CommitTrustedOutcome`
  when the coordinator became async. Nothing consumed it yet, so this broke no callers.

## Open issues filed

- **FIXED by `overview-` while I was reporting.** `MailPolicyEngine` allowed on zero covered weight;
  they added `PolicyOptions.MinimumCoverageForAllow` (default 0.30), so a total semantic outage now
  yields a bounded **Hold** rather than an Allow. My `DeclineResponsibilityOnSemanticOutage` guard
  still turns that into a **Defer**, see decision 4. A local-only deployment must now declare
  *both* knobs (`DeclineResponsibilityOnSemanticOutage = false` **and**
  `Policy.MinimumCoverageForAllow = 0`); two tests bracket that, one asserting the Hold when only
  one knob is set.
- **RESOLVED by `overview-`:** `POST /v1/submissions` must now **require** `Idempotency-Key` and
  `400` without it. The MTA/Cloudflare ingress paths are explicitly unaffected, no client key
  exists there by construction and dedup is the upstream MTA's concern. `host-` was told as
  coordination, not instruction (it is their route).
- `host-`'s `src/StyloMail.Host/Endpoints/SubmissionsEndpoints.cs` did not compile
  (`ClaimsPrincipal` has no `TenantId`/`PrincipalId` extension), theirs, mid-flight, untouched.

## Gotchas worth remembering

- **BUS HAZARD: `reply_to_thread` archives but does NOT deliver.** It returns
  `sent → archive/outbox/<thread>.reply.md`, which reads as delivered and is not, the recipient
  never sees it. **Use `send_message` for anything a peer is waiting on**; use `reply_to_thread`
  only to close a thread the recipient will next see anyway. I lost four replies to this and left
  `host-` blocked on answers that already existed. `overview-` re-sent everything after `queue-`
  found it.
- Bare `grep` is a `ugrep -I --ignore-files` wrapper that **silently skips** files. Use
  `/usr/bin/grep` for any absence check.
- `Microsoft.Data.Sqlite` `*Async` methods are synchronous, concurrency tests need `Task.Run`.
- Never read, print or reference `jevkey.pvt`.
