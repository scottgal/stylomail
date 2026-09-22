# `assess-` — checkpoint

*Written 2026-09-22. This is the authoritative copy; `assess--context.md` is now a pointer to it.*

## Identity and scope

Composition root for StyloMail. Own `src/StyloMail.Assessment/` and
`tests/StyloMail.Assessment.Tests/`. Wire MIME + semantic classifier (+ cache decorator) + adaptive +
policy + queue into the single `IMailAssessor` the host consumes. **Never edit another project** —
message its owner and copy `overview-`.

Fleet: `overview-` (root) · `mime-` · `adaptive-` · `queue-` · `host-` · `ingress-` · `transport-` ·
`access-` · `desktop-` · `assess-` (me).

## Build and current claims

```bash
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Assessment.Tests/StyloMail.Assessment.Tests.csproj   # 124 passed, 0 failed
dotnet build src/StyloMail.Assessment/StyloMail.Assessment.csproj               # 0 errors
```

**Report BOTH claims, and never claim a clean sweep you did not run.** Two fleet rules:
`dotnet build StyloMail.slnx` (compile breaks) **and** `dotnet test StyloMail.slnx` (a suite that
passes one run in four). Build your own project to avoid being blocked by another lane's transient
red; build *and test* the solution before declaring done, **attributing any non-mine failure by name**.

## Files

```
src/StyloMail.Assessment/
  MailAssessor.cs                  the IMailAssessor; pipeline steps 1-8
  MailAssessorOptions.cs           limits, version stamps, learning rule, outage guard
  AssessmentValidation.cs          step 1 rules + AssessmentRules ids
  AssessmentPipeline.cs            one-call factory over the shared SQLite + spool
  EvidenceVectors.cs               semantic evidence -> DimensionVector (masked, never zero)
  ProfileCoordinator.cs            "append, or decide?" - two store ops, two counters
  Ports.cs                         IRawMessageSource, IAdaptiveProfileStore,
                                   IMessageAcceptanceQueue, IAssessmentPolicyContextSource
  Learning/TrustedLearningGate.cs  the only door to trusted learning
  Campaign/RecentCampaignWindow.cs bounded per-tenant; holds vectors + digests only
  Campaign/CampaignNearDuplicateDetector.cs
  Semantic/SemanticCacheOptions.cs
  Semantic/ClassifierInputCanonicalizer.cs   canonical form + digest (incl. the profile)
  Semantic/SemanticCacheKey.cs
  Semantic/SecurityBearingFingerprint.cs     + PaymentIdentifiers
  Semantic/CachedSemanticAssessment.cs       + SemanticCoverage
  Semantic/ISemanticCacheStore.cs            + InMemorySemanticCacheStore (LRU/LFU, expiry)
  Semantic/SingleFlight.cs
  Semantic/SemanticCacheClassifier.cs        the decorator + IContextualSemanticClassifier
tests/  TestSupport.cs · SemanticCacheTests.cs · CacheKeyTests.cs · MailAssessorTests.cs ·
        ProfileCoordinatorTests.cs · AssessmentPipelineIntegrationTests.cs · SharedStateTests.cs
```

## Pipeline order — fixed, do not redesign

**1** validate → **2** MIME parse + deterministic evidence → **3a** read profile snapshots + reserve
outbound budget → **4** semantic evidence (cache hit / provider / explicit unavailable) → **5**
behavioural + campaign comparison → **6** policy → **3b** write observed counters → **7** accept or
decline → **8** learning gate.

The one deliberate arrangement: **the counter write lands at 3b, after the comparison and before
policy.** It must see the profile as it was *before* this message (the spec's "never normalise a
message with its own evidence first"), and the observation carries this message's semantic vector,
which does not exist until step 4. Budget reservation still precedes provider spend.

## Decisions not to re-litigate

1. **Unavailable is never memoised.** The semantic cache stores nothing when the provider answered
   nothing — writing an outage down turns it into one lasting the entry's lifetime.
2. **`ServeStaleOnProviderUnavailable` defaults false.** Off, an outage stays an outage.
3. **`DeclineResponsibilityOnSemanticOutage` defaults true** — a total semantic outage gives index 0
   over 0 coverage, which policy once returned as `Allow`. `overview-` has since added
   `MinimumCoverageForAllow`. A local-only tenant must set **both** knobs.
4. **Assessment-only**: no delivery, no learning, **no live traffic accounting**. Still reads profiles
   and still records campaign evidence (the window gates nothing and holds no assessments). Kept out
   of acceptance by an **explicit guard**, with a test using a *durable* reference so the guard is
   shown to be load-bearing rather than masked by `RequireDurable`.
5. **The assessor is the only component that accepts.** `overview-` ruled after `host-` and I both
   called `AcceptAsync` under different keys — two acceptances the queue could not dedupe.
6. **The queue's `IdempotencyKey` is the caller's**, passed through; **never minted**. My original
   `assessmentId` silently replaced the client's replay contract with one we could not keep. No
   fallback key: inventing one looks like protection while providing none.
7. **`MailAssessment.SubmissionId` / `.Submission`** — non-null exactly when a durable row exists;
   built through `AcceptanceOutcome.Accepted/Refused` so acceptance and decline cannot both be
   reported. Also set on a *duplicate* replay, carrying the **existing** id.
8. **Profiles have exactly two operations, neither optimistic** — the store's, not mine.
   `ApplyObservation` (append, the burst path) and `Update<T>` (a decision inside the write lock, the
   rare path). Both take the write lock before reading. **Deleted:** a 256-stripe gate array, a
   bounded CAS retry loop, `ProfileUpdateConflictException`, the `Save` port member. The retry loop
   could not survive a burst (measured: 4 attempts, 4 conflicts).
9. **Acceptance requires a durable `PayloadReference`.** The transport must spool for real. A
   reference durable *in form* but naming no stored payload gets its own diagnostic — that case cost
   `host-` an hour because it looked like a storage fault.
10. **Null sender** — `Core.SenderAddresses.IsNullSender`, one definition. Refused **unconditionally
    on outbound** (we do not originate bounces); inbound DSNs are ordinary mail. Three call sites used
    to crash on an empty identity; one `NullSender(...)` helper now serves all.

## Behavioural profile (threaded 2026-09-22)

`BehaviouralProfileEncoder.Encode(senderSnapshot, now, messageRecipients, options)` feeds
`SemanticMailInput.Profile`. **The profile is part of the classifier input and therefore of the cache
key** — my canonicaliser initially ignored it, which meant two messages with identical content and
different sender behaviour shared one cached assessment. All **19** `BehaviouralProfile` fields plus 3
on `DimensionMovement` are now encoded, **null distinct from zero**.

Two things worth not re-deriving:
- **`FeatureVector` omits rate features when zero time has elapsed in the bucket** (a rate over zero
  elapsed time is undefined, not zero). A frozen test clock produces a rate-less sample and a green
  assertion over nothing. **Advance the clock in rate tests.**
- **The field-count tripwire is the highest-value test in this lane.** It failed within the hour when
  `adaptive-` added `RecipientDistinctnessIsFloor`, with "encode it" rather than silent blindness.

**Open with `overview-`:** `CommitTrustedOutcomeAsync` promotes a caller-supplied vector, so a caller
assembling one from evidence omits rates. `BuildTrustedSample(tenantId, direction, senderIdentity,
at, provenance, label)` exists for them — **outbound only**, because an inbound key is qualified by
authentication provenance and cannot be named from the identity alone.

## Escalated, not mine to decide

- **Novelty is permanently null on high-fan-out senders** (recipient history saturates) — exactly the
  compromised-account shape. `overview-` is replacing it with a Bloom filter (no false negatives).
- **Nothing in my pipeline reads novelty.** Checked, not applicable. The field will carry real values
  more often once the Bloom filter lands.

## Rules that keep paying off

- **Verify per run, not per session.** A tree-cleanliness check is valid only for the window it was
  taken in. Check `.styloagent/tools/.mutation-sweep.lock` **before** each run you intend to trust.
- **The lock is the load-bearing signal; absence of `.bak` proves nothing.** `host-` corrected this and
  is right: *my own sweeps* mutated source in place with backups in `/tmp`, so no in-tree `.bak` would
  ever have been findable during one of them — the residue check would have reported clean while a
  sweep was running. Two signals, and only one of them can be trusted on its own. A check that stays
  silent while the thing it checks for is happening is not a check.
- **Behaviour gets tests; prose gets nothing.** I once reported a documentation change my tooling had
  silently skipped. Use `Edit` (errors on a non-matching anchor) and **grep for the thing you claim to
  have written.**
- **Re-measure before attributing a cause.** "Flaky" and "deterministic" are claims about a
  distribution; three runs inside one edit window measures a *tree state*. Four wrong claims came out
  of that one thread, all arithmetically right.
- **Enumerate your own lane as a possible cause for others**, not only other lanes as a cause for you.
- **Deleting a mechanism invalidates the reasoning that chose it.** Grep for the comments that
  justified it — a stale justification can make the next person *rebuild* what was deleted.
- **Declared but unexercised surface**: a Core field I was the sole producer of sat null on every
  message; three mandatory limits had no test; an eviction policy's branch was untested. Sweep for it.
- **Favourable evidence is the kind that goes unexamined.** Correcting a claim *downward* is the harder
  and more valuable direction.
- Never read, print or reference `jevkey.pvt`. Never `git add`/`git commit`. Bare `grep` is a `ugrep`
  wrapper that silently skips files — use `/usr/bin/grep` for absence checks.
- `Microsoft.Data.Sqlite` `*Async` methods are synchronous: concurrency tests need `Task.Run`.
- **Bus hazard: `reply_to_thread` archives but does NOT deliver.** Use `send_message` for anything a
  peer is waiting on; `reply_to_thread` only to close a thread the recipient will next see anyway.

## Mutation evidence

**39 mutations across 14 rounds, all RED after corrections.** Three of my own mutations were
themselves unsound and were reshaped rather than counted — an unused-field analyzer error (a build
failure is not a passing mutation), a race-window widening the test could not hit, and an anchor that
did not match. **My ad-hoc edit helper printed "ok" unconditionally**; that is how a prose change went
unmade while I reported it done.
