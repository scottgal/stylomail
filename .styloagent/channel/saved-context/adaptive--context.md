# `adaptive-`, saved context

## Identity and scope
Behavioural profiles, drift/velocity/acceleration and trusted learning for StyloMail.
Owns `src/StyloMail.Adaptive/` and `tests/StyloMail.Adaptive.Tests/`, nothing else.
Parent: `overview-`. Siblings: `mime-` (MIME parsing / deterministic extraction).

Hard boundaries: do not edit `StyloMail.Core`, `StyloMail.Persistence`, `StyloMail.Jev`,
`StyloMail.Mime`, `.styloagent/spec.md`, `email-proxy-spec.md`. Contract changes go through
`send_message` to `overview-`. Never read or reference `jevkey.pvt`.

## State
- Branch: `main` (no worktree, spawned with `worktree: false`, so no `wrap_up()`).
- Status: **complete and green**. `dotnet test tests/StyloMail.Adaptive.Tests` → 140 passed, 0 failed.
  **Whole suite green** (`dotnet test StyloMail.slnx`): 812 tests, 0 failures, all 11 projects, as of
  2026-09-22 07:1x. `dotnet build StyloMail.slnx` → 0 errors, 1 warning (CS0168 in Host, not mine).

## Stale justifications, a live hazard in this lane (found 2026-09-22)
`assess-`'s generalisation: *deleting a mechanism silently invalidates the reasoning that chose it,
and the reasoning outlives the deletion because nothing fails.* Applying it found two in my code:
- **The store's class comment** still told callers to "serialise per principal at the call site",   true before `ApplyObservation`/`Update` existed, **the opposite of true afterwards**, and it would
  have led a reader to rebuild the exact caller-side gate `assess-` had deleted. Rewritten: writes
  through those two methods are safe for the *same* profile and **no caller-side serialisation is
  needed**.
- **`AdaptiveProfile`'s remarks** claimed `Save` had no compare-and-swap. It has had one since the
  revision landed. Rewritten.
Also folded in `assess-`'s correction that `ApplyObservation` vs `Update` is **shape, not safety**
(both take the write lock before reading).

**Rule for this lane: after removing or superseding any mechanism, re-read the comments that
justified it.** Nothing fails when they go stale, and they are believed.

## Per-call timestamps (2026-09-22, ruled by `overview-`), no clock in the ledger
`TryReserve`/`Release`/`Remaining` all take `DateTimeOffset at`. **No `TimeProvider` anywhere in the type.**
Rationale (overview-): *"a stated requirement rather than an enforced one is the defect"*, a constructor
clock makes correct replay depend on the harness fixing the options clock as well as the context clock.
`assess-`'s `MailAssessorOptions.TimeProvider` knob is **gone**, verified.

**The change made a latent bug live:** with caller-supplied instants the reservation list is no longer
sorted, and front-only pruning had assumed it was. An out-of-order instant left an expired reservation
counted behind a live one, budget silently tighter than configured, no error. `Prune` now filters the
whole list. Pinned by `AnOutOfOrderInstantStillExpiresItsReservation`; front-only mutation → exactly
that test RED. **Anything relying on "instants arrive in order" here is wrong.**

Mutations (4/4 RED): front-only prune → 1; never prunes → 8; boundary one tick early → 2; oldest-first
release → 1. **Three mutation artefacts hit in this lane now**, before believing any verdict, confirm
the mutation both *built* and *changed behaviour*. TOOTHLESS and INCONCLUSIVE both mean "measured
nothing" and neither is evidence about the code.

## `Update<T>`, the transactional decision callback (2026-09-22, requested by `assess-`)
`SqliteAdaptiveProfileStore.Update<T>(key, at, Func<AdaptiveProfile, T>)`: load → delegate → write in one
`BEGIN IMMEDIATE` transaction, returning the delegate's result. Matches `assess-`'s
`IAdaptiveProfileStore` port (`src/StyloMail.Assessment/Ports.cs:182`) exactly.

**Why:** a promotion racing a burst **lost promotions** (`conflicts=4 saves=4 observed=254`, first
promotion exhausted its retries and threw; flaky, read as noise). An operator promoting *during* the
incident that produces a burst is when the write most needs to succeed. CAS-and-retry cannot fix that;
removing the conflict can.

**Three properties, two of them sharp edges:** the delegate holds SQLite's single write lock (keep it
in-memory, no I/O, no `await`, **no nested store call → deadlock**); it **always writes** even if
nothing changed, so it is not a read path (`Load` is); a throwing delegate rolls back and propagates
unchanged.

**Mutation-verified:** reverting `Update` to load-outside-transaction + CAS reddens **exactly**
`PromotionsRacingABurstNeitherConflictNorLoseObservations`, the test discriminates the fix from the
design it replaces. Deferred-vs-immediate remains **undiscriminated** (same known caveat as
`ApplyObservation`).

**Current reds that are NOT mine:** `StyloMail.Assessment.Tests` `CS0535` (`FakeProfileStore` /
`BurstInterposingProfileStore` lag `assess-`'s own interface, told them); `StyloMail.Host` →
`StyloMail.Transport` types. My project and `StyloMail.Assessment` both build.

## Rolling window landed (2026-09-22), the lifetime-cap defect is FIXED
`SendingQuotaLedger(recipientsPerWindow, window, timeProvider)` + an overload defaulting to
`DefaultWindow = 1 hour`. `TryReserve`/`Release` signatures and return contract unchanged.
500 recipients/hour. **`DefaultWindow` is marked unvalidated in code**, do not present it as tuned.

**`Remaining` now means "remaining in the current window"** and reopens on its own; `Release` cannot
give back budget that already expired (returns 0). Reserve entries are `(At, Recipients)`, pruned from
the front, released from the back, **most-recent-first**, deliberately the opposite of the phrasing in
`overview-`'s mutation list; rationale sent to them and the order is pinned by test either way.

Restart behaviour documented: in-memory ⇒ a restart grants a fresh window, tolerable *because* it is a
window (a restart only advances something that reopens anyway). Stops being tolerable if this must
bound anything across restarts.

Mutation-verified, `overview-`'s four candidates: never prunes → 6 RED; drops entries still inside the
window → 16 RED; boundary one tick early → 2 RED; oldest-first release → 1 RED. Restore by sha256.

**Two mutation artefacts to remember** (both cost a cycle this session): a mutation that stops using a
field fails to compile → INCONCLUSIVE, never a pass (trap 1); and a mutation that does not change
behaviour (read `First` but still remove `Last`) is a no-op → it reports TOOTHLESS while proving
nothing. Reshape it before believing the verdict.

**`assess-` was already adapted**: `MailAssessor.cs:187` uses the 3-arg ctor and
`OutboundRecipientBudgetWindow` defaulting to `SendingQuotaLedger.DefaultWindow`. `StyloMail.Assessment`
builds. Solution build is red from `StyloMail.Host`→`StyloMail.Transport` types (not mine; my project
and Assessment both build).

## `SendingQuotaLedger.Release` now returns the amount actually released (2026-09-22)
Was `void` and silently clamped an over-release to zero, the same silent-success shape as the bus
hazard: the caller's books diverge from the ledger's with nothing to notice. Now returns `int`.

**Contract:** a return smaller than requested is a **discrepancy to account for, not a routine
clamp**. Clamping at zero is kept (safe direction, can never manufacture headroom) and over-release
is deliberately **not** an error, releasing the same reservation twice on a retry path is
legitimate, so throwing would put a crash on a hot path. `overview-` decided this.

Mutation-verified (`/tmp/adaptive-release-mutation.py`): reporting the requested amount instead of the
actual → 4 RED; removing the clamp → 3 RED.

**RESOLVED, never a gap, and it found a real defect.** `assess-` *was* reserving and never releasing,
which permanently deferred any principal that used its budget (every blocked message kept the quota
blocked). They fixed it: release only when the reservation **succeeded** *and* acceptance did **not**
happen, releasing an unsuccessful reservation un-exhausts the quota on every message so it never
binds at all. They now call `TryReserve` + `Release` and count shortfalls in
`MailAssessorStatistics.BudgetReleaseShortfall`.

My earlier "about to consume `Release`" claim was asserted without checking the far end; the check is
what surfaced the defect, so the wrong-for-the-right-reason message did real work.

## OPEN DEFECT (raised to `overview-`, awaiting decision)
**`SendingQuotaLedger` has no time dimension, so it is a lifetime cap, not a rate limit.** Verified: no
`TimeSpan`/`TimeProvider`/window anywhere in the ledger; no reset path by design (eviction deliberately
grants no fresh quota); default budget 500. Consequence: a principal may send 500 recipients **ever**, one newsletter and it is permanently deferred. `assess-`'s fix does not close this (accepted sends
still consume forever). Also **in-memory**, so a process restart grants everyone a fresh lifetime budget.

Proposed (all decided centrally, not by me): injected `TimeProvider` + rolling window, `TryReserve`/
`Release` signatures unchanged so `assess-`'s call sites do not move, only the meaning of "remaining"
changes. Persistence is a separate question, flagged not bundled. **Do not change the semantics before
`overview-` answers.**

## `ApplyObservation`, the ingest delta path (added 2026-09-22)
`SqliteAdaptiveProfileStore.ApplyObservation(key, observation, at)` does load-observe-write in one
`BEGIN IMMEDIATE` transaction. It exists because CAS alone was **not** enough: under a burst many
callers hold the *same* profile, so only one can win a round and the rest must reload and retry, `assess-` proved this loses observations (16 concurrent observers of one sender failed after 4
attempts). 16 concurrent deltas now all land, zero conflicts, test-asserted.

**Unverified claim, deliberately documented as such:** switching the delta transaction from
`deferred: false` to deferred leaves **all 120 tests green** (mutation-checked, 5 consecutive runs).
So `BEGIN IMMEDIATE` is *not* discriminated by any test, SQLite's refusal to upgrade a stale
snapshot is what protects the deferred version, and that is the engine's behaviour rather than ours.
The comment in the source says exactly this. Do not re-mutate it expecting a red test.

**`Save`'s CAS window is closed, verified, not assumed.** `assess-` was unsure whether two creators
of a brand-new profile could both read revision 0 and both pass; `ConcurrentCreatorsOfABrandNewProfile
CannotBothReportSuccess` shows `successes=1 conflicts=15 storage_errors=0`. The test counts reported
successes against what actually landed, which is what would expose a double-pass.

## `readonly` is not immutability (adopted fleet-wide, 2026-09-22)
The one generalisation from this lane that `overview-` adopted. A `private readonly Dictionary` reads
as safe and is the exact object that corrupts. Assertions are therefore two-part: **immutable** types
get a reflection tripwire; **mutable-but-shared** types (quota ledger, incident log) get a contention
test, because a field-shape check would pass on an unsafe object and imply a guarantee it cannot give.

## Optimistic concurrency (added 2026-09-22, requested by `overview-`)
`SqliteAdaptiveProfileStore.Save` now compares-and-swaps and throws
`ProfileVersionConflictException` (NOT a `SqliteException`, so a busy-lock handler cannot swallow it).
`AdaptiveProfile.PersistedRevision` is the token; `Load` sets it, `Save` bumps it.

**Token is a revision, not `baseline_version`**, the baseline version only moves on promotion, so an
observation-only save would carry an unchanged token and a concurrent write would overwrite it
unnoticed. Stored in `adaptive_profile_revision` (my own table; the shared `profiles` table has no
spare column). The CAS is checked **before** any write, in the same transaction, so a refused write
leaves the profile exactly as the winner left it.

Mutation-verified (`/tmp/adaptive-cas-mutation.py`, restore verified by sha256): CAS-never-fires → 4 RED;
revision-never-advances → 6 RED. `assess-` can now drop the per-profile-key serialisation in
`ProfileCoordinator`.

### Superseded: the old known limitation
The paragraph below was true until the CAS landed; kept only so a cold start understands why the
serialisation workaround existed in `assess-`'s `ProfileCoordinator`.

## Thread-safety / shareability (answered for `assess-`, who hosts these)
**Safe to share one instance per host** (immutable; asserted by a reflection tripwire in
`SharedStateTests`): `ProfileKeyHasher`, `RobustScaleModel`, `DimensionVector`,
`BehaviouralEvidenceEvaluator`, `SqliteAdaptiveProfileStore` (stateless; opens a connection per call).

**Must NOT be shared**, per-principal mutable state: `AdaptiveProfile` (and its `BucketSeries` /
`BehaviouralBucket` / `RegimeCandidate`), one instance per profile.

**Shareable and now locked** (were unsafe until 2026-09-22): `SendingQuotaLedger` and `IncidentLog`
carry a `Lock`. `TryReserve` is check-then-act, so unlocked it *over-grants*, and the quota is the
only thing bounding how much a late detection lets escape.

**Known limitation, documented not hidden:** `SqliteAdaptiveProfileStore.Save` is a whole-row upsert
with **no compare-and-swap**, so two threads that load → mutate → save the same profile lose one
update. Observed counters reading low weakens abuse bounding. Serialise per principal at the call
site until an optimistic version check is added. (An earlier comment in `AdaptiveProfile` claimed the
store already did this, it did not. Corrected.)

**Mutation-verified** (`/tmp/adaptive-lock-mutation.py`, restore verified by sha256): removing the
`TryReserve` lock → 2 RED; removing the `IncidentLog.Record` lock → 1 RED. Neither toothless.
Also replaced a quota test that was toothless **by construction** (16 threads × 5 recipients can
never exceed a 500 budget) with an exact `capacity - granted + released` accounting invariant.
- Not committed, the mission forbids `git add` / `git commit`; the operator commits.

## Delivered (19 src files, 3498 lines; 11 test files, 2054 lines)
- `Profiles/ProfileScope.cs`, bounded scopes, `ProfileScopeTrust` (domain is never account trust),
  `ProfileScopes` key builders, authentication provenance from trusted verifiers only.
- `Profiles/ProfileKeyHasher.cs`, tenant-scoped HMAC-SHA256 pseudonyms.
- `Profiles/DimensionVector.cs`, masked-or-measured vectors; a missing sample cannot carry a value.
- `Profiles/ObservedState.cs`, `Profiles/TrustedBaseline.cs`, the two stores, never conflated.
- `Profiles/AdaptiveProfile.cs`, the aggregate: observe, promote, freeze, regimes, rollback, dehydrate.
- `Scoring/RunningMoments.cs`, `Scoring/RobustScaleModel.cs`, Welford moments, diagonal robust
  standardization, variance floors, z clamping, RMS distance over compared dimensions only.
- `Temporal/BucketSeries.cs`, `TrendAnalyzer.cs`, `TrendNarrative.cs`, `FanOutEvaluator.cs`.
- `Learning/Ewma.cs`, `RegimeCandidate.cs`, `SendingQuotaLedger.cs` (+ `IncidentLog`).
- `Signals/BehaviouralEvidence.cs`, `Signals/BehaviouralEvidenceEvaluator.cs`, Core `Evidence` out;
  the evaluator is the only place that reads a clock (`TimeProvider`).
- `Storage/SqliteAdaptiveProfileStore.cs`, profile persistence + `adaptive_baseline_dimension`.

## BUS HAZARD (urgent, 2026-09-22), read before replying to anyone
**`reply_to_thread` archives a thread but does NOT deliver its content to the recipient's inbox.**
It returns `sent → archive/outbox/<thread>.reply.md`, which reads as delivered and is not.
- **Answering a peer who is waiting on you → `send_message`.** Always.
- `reply_to_thread` only to close a thread whose recipient will see it anyway.
- **Verify at the far end by where the file actually is, never by the tool's return string.**
  Read the return string as a *hint only*: `send_message` has been observed returning
  `sent → inbox/<recipient>-<slug>.md` while the file actually landed in `archive/inbox/`
  (message to `watchdog-`, which has no live session, it is not in the fleet roster).
- **The tell is the path prefix: anything under `archive/` was not delivered into a live inbox.**
- **Folder lifecycle:** `inbox/` = pending, not yet drained (reading does NOT drain it);
  `archive/inbox/` = completed or undeliverable; `archive/outbox/` = `reply_to_thread` output,
  never delivered. Do not conclude "NOT DELIVERED" from `inbox/` alone, I nearly reported a
  false second hazard that way.
- Live prefixes are only: overview-, mime-, adaptive-, queue-, host-, assess-, transport-, access-.
- My CAS completion report to `overview-` was lost this way and re-sent by `send_message`
  (verified: `inbox/overview-cas-is-done-and-green-re-sending-my-reply-to-thr.md`, 4211 bytes).

## Pending decision from overview-
`SendingQuotaLedger.Release` returns `void` and silently clamps an over-release to zero, so a caller
that releases more than it reserved gets no signal, the same "success without the outcome" shape as
the bus hazard, found in my own lane. Proposed fix: return the amount actually released
(source-compatible). **Not applied**, semantics decision on an interface `assess-` is about to
consume; overview- may prefer a throw. Awaiting the call.

## Infra gotchas
- `dotnet` is NOT on PATH. Every shell needs:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, `net10.0`. Solution is `StyloMail.slnx`.
- **Analyzers are errors**: CA1822/CA1823/CA1826/CA1861 fail the build. Write analyzer-clean code.
- The store persists `profile_key` with a `#outbound` / `#inbound` suffix. The shared `profiles`
  PK is (tenant, scope, key) with no direction, and this engine keeps inbound and outbound
  statistics distinct, so direction is folded into the stored key rather than the table migrated.
- `SqliteSchema.EnsureCreated` is now genuinely idempotent (fixed by `queue-`/`overview-`; pragmas
  moved before `BeginTransaction`). My `TableExists` workaround was removed, call it unconditionally.
- No async anywhere in this lane, so the `Microsoft.Data.Sqlite` async-is-synchronous trap
  (fleet-wide gotcha, 2026-09-22) does not apply. The store is synchronous and single-threaded by
  design; concurrency belongs to the caller.

## Suppression-rule mutation matrix (2026-09-22, trap 3 from the amended advisory)
Driver: `/tmp/adaptive-mutation-check.py`, replaces each `suppressions.Add(Rule);` with a discard
(keeps the mutation compiling, per trap 1), runs the suite, restores, and verifies the restore by
sha256 before trusting the result (trap 2). Result: **all six rules RED, none toothless.**

| Rule mutated | Guard tests that went red |
| --- | --- |
| BaselineUnavailable | `AMissingBaselineSuppressesDerivativesRatherThanAssumingNormal` |
| RegimeChange | `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`, `ARegimeChangeSuppressesTheDerivative` |
| DimensionSchemaChange | `ADimensionSchemaChangeSuppressesTheDerivative` |
| InsufficientSupport | `ASingleBucketProducesNoVelocityOrAcceleration`, `ASuppressedWindowSaysSoInsteadOfReportingZero` |
| SparseBucket | `AnEmptyBucketSuppressesTheDerivativeAcrossIt` |
| LongGap | `ALongGapIsMeasuredInElapsedTimeNotInBucketCount`, `ALongGapSuppressesTheDerivative` |

**Fixed as a result:** `Analyze` had *two* conditions reporting `InsufficientSupport` ("no populated
buckets" and "fewer than two"), plus an unreachable third in `Compute`. Dropping any one left
another adding the identical reason, so a mutation was invisible. Now one condition, one place.
Also: every suppression rule produces the same *observable* outcome ("no derivative evidence"), so
the `suppressions` list, not `VelocityAvailable`, is the load-bearing assertion in every test here.
Never remove those reason assertions; without them the rules are indistinguishable.

**Behaviour change worth knowing:** with fewer than two populated buckets the analyser now returns
early, so `SparseBucket` is no longer co-reported alongside `InsufficientSupport` for a single
bucket. The dominant reason already explains the case fully.

### Round 2, condition mutations (variant 2: a guard redundantly covered by another guard)
Round 1 only proved each *reason is reported*. Round 2 weakened each guard's **predicate** so it can
never be true, leaving the `Add` in place, which tests whether the condition itself is doing the work.
Driver: `/tmp/adaptive-condition-mutation.py`. Result: **all six guards RED, none doing no work.**

`BaselineUnavailable` → 1; `RegimeChange` → 2; `SchemaChange` → 1; `InsufficientSupport` → 6;
`SparseBucket` → 1; `LongGap` → 2. `restored cleanly: True`; `guards doing no work: none`.

Notably `LongGap` and `SparseBucket` are correlated under the shipped window configs (an empty
interior bucket trips both) yet each is independently detected, so neither is masking the other.
Ledger-level distinguishability is tested too: `ARegimeChangeSuppressesDerivativesUntilTheWindowClears`
asserts the `suppression` attribute value `regime_change` reaches the `Evidence`, not just the
`TrendResult`.

## Mutation-verified invariants (2026-09-22)
Per the fleet "a test that cannot fail is not a test" directive, two invariants were mutated and
each confirmed to redden its guard tests, then reverted (no residue):
- Masked dimensions filled with `0.0` → `AMaskedDimensionIsExcludedAndNeverFilledWithZero` and
  `CoverageReportsHowMuchOfTheProbeWasActuallyCompared` both FAIL.
- Freeze check removed from `Promote` → `FreezingStopsPromotionButNotObservation` and
  `TheBaselineVersionAdvancesOnlyOnPromotion` both FAIL.
Note: `CoverageReportsHowMuchOfTheProbeWasActuallyCompared` had been silently lost in an earlier
full-file rewrite of `RobustScoringTests.cs`, the mutation check is what surfaced the gap. It is
restored.

## Known issues reported upward
- `SqliteSchema.EnsureCreated` second-call throw, reported, and now fixed upstream.
- `StyloMail.Queue` did not compile at last check (CS0103/CS1061 in `QueueStore.cs`), not mine.

## Deliberately not done
- Full Mahalanobis/covariance scoring (spec puts it after sample support is adequate).
- Campaign/near-duplicate grouping (spec §8), belongs to the campaign owner, not this module.
- Thresholds and half-lives are unvalidated engineering defaults, not tuned values.
- `SqliteAdaptiveProfileStore` does not persist the regime candidate mid-evaluation (only the
  promoted baseline); a restart mid-candidacy restarts the candidacy.
