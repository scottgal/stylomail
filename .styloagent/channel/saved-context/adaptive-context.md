# `adaptive-` — saved context

*Canonical checkpoint. `adaptive--context.md` is a pointer to this file.*

## Identity and scope
Behavioural profiles, drift/velocity/acceleration and trusted learning for StyloMail.
Owns `src/StyloMail.Adaptive/` and `tests/StyloMail.Adaptive.Tests/` — nothing else.
Parent: `overview-`. Siblings: `mime-`, `queue-`, `host-`, `assess-`, `transport-`, `access-`.

Hard boundaries: do not edit `StyloMail.Core`, `StyloMail.Persistence`, `StyloMail.Jev`,
`StyloMail.Mime`, `.styloagent/spec.md`, `email-proxy-spec.md`. Contract changes go through
`send_message` to `overview-`. Never read or reference `jevkey.pvt`.

## State
- Branch `main`, no worktree (spawned `worktree: false`, so no `wrap_up()`). Not committed —
  the mission forbids `git add`/`git commit`; the operator commits.
- **Lane complete and signed off by `overview-`.**
  - `dotnet test tests/StyloMail.Adaptive.Tests` → **181 passed, 0 failed**
  - `dotnet build StyloMail.slnx` → **0 errors, 0 warnings**
  - Whole fleet suite (`dotnet test StyloMail.slnx`) was 812/0 across 11 projects at last full run.
- Idle, standing by.

## Delivered
`src/StyloMail.Adaptive/` — ProfileScope, ProfileKeyHasher, DimensionVector, ObservedState,
TrustedBaseline, AdaptiveProfile, RecipientHistory, RecipientBloomFilter · Scoring/RunningMoments,
RobustScaleModel · Temporal/BucketSeries, TrendAnalyzer, TrendNarrative, FanOutEvaluator ·
Learning/Ewma, RegimeCandidate, SendingQuotaLedger (+IncidentLog) · Signals/BehaviouralEvidence,
BehaviouralEvidenceEvaluator, BehaviouralProfileEncoder · Storage/SqliteAdaptiveProfileStore.

## Behavioural profile encoder (spec §11)
`BehaviouralProfileEncoder.Encode(profile, at, messageRecipients?, options?)` → `Core.BehaviouralProfile`.
Gives the classifier the behavioural context it was judging without. `at` per call; no clock inside.

- **`Encode` is total — no path returns null.** `SemanticMailInput.Profile == null` means "the pipeline
  did not populate it"; a non-null profile with `ProfileAvailable: false` means "we looked and found
  nothing". Availability needs *both* observed attempts and trusted support empty — approved history
  alone counts as knowing a sender. `ColdStart` is false whenever unavailable (check availability
  first; the two flags are distinct by design).
- **Every observation field is null when unavailable**, never zero.
- **Measured cost 6.95 µs mean** (20k iterations, 500 observations + 50 promotions, 3 semantic + 2 rate
  dimensions) ≈ 0.03% of the 20 ms local p95 target. Probe deleted; no timing assertion shipped.
- **No verdict-shaped field**, and the guard is **self-verifying** — the detector is asserted to fire on
  `RiskDimension`, `MailAssessment`, `RecipientDisposition`. (`Evidence` is *not* verdict-shaped; I
  mispredicted that before checking.)

## Recipient tracking (two structures, deliberately)
`RecipientHistory` (capacity **256**, window **30 days** — both **unvalidated**) + `RecipientBloomFilter`
(4096 @ 1% — **unvalidated**). `ProfileObservation.RecipientKeys` (additive, nullable, hashed).

- **The capped set keeps distinct counts; the filter keeps novelty.** The set is cheap and exact while
  it has room, and a **floor** once `Truncated`. The filter never truncates and has no false negatives.
- **The direction of the error decides the encoding.** A count can only *under*-state when truncated →
  emitted as a floor with `RecipientDistinctnessIsFloor` set. Novelty *over*-states → `null` when it
  cannot be established, never zero.
- `FanoutLastHour` stays **addresses**, deliberately distinct from distinct-people — the *gap* between
  the pair is the fan-out signal.
- **`IsComplete`:** false when a history was not restored. An empty filter reports *every* recipient as
  novel, so an unrestored one must not answer.
- **Typed properties:** a `RecipientBloomFilter` empty is not evidence of absence; `ToBytes`/`FromBytes`
  exist for persistence; SHA-256 double hashing is **deterministic on purpose**
  (`string.GetHashCode()` is per-process seeded and would disagree with a persisted filter).

**THE HAZARD THAT MADE PERSISTENCE MANDATORY:** `ApplyObservation` loads from the store on *every
call*. An unpersisted filter would start empty each time and report every recipient as novel —
manufacturing the loudest signal in the profile, constantly, for the most-established senders.
Persistence is part of the guarantee, not an optimisation. The store marks a history incomplete for a
row with observed traffic but no stored filter.

**Dependency, still open:** `BaselineFanoutPerHour` / `BaselineMessagesPerHour` / the fan-out *narrative*
need the trusted baseline to model `rate.*` features. Rate features are synthesised per bucket, so a
promotion path approving only `semantic.*` leaves them unmodelled → null. `assess-` confirmed **nothing
promotes them today**. Do **not** fabricate a baseline.

## Quota ledger
`SendingQuotaLedger(recipientsPerWindow, window)` — 500 recipients per **hour**, `DefaultWindow = 1h`,
**unvalidated**. `TryReserve`/`Release`/`Remaining` all take `DateTimeOffset at`; **no `TimeProvider`
anywhere in the type** ("a stated requirement rather than an enforced one is the defect"). `assess-`'s
`MailAssessorOptions.TimeProvider` knob is gone.

- `Remaining` means "in the current window" and reopens on its own.
- `Release` returns `int` — **the amount actually released**. A shortfall is a discrepancy to account
  for, not a routine clamp. Clamps at zero; over-release is deliberately not an error.
- **Prune filters the whole list, not front-only.** Caller-supplied instants are not ordered, and
  front-only pruning left an expired reservation counted behind a live one. Anything here that assumes
  "instants arrive in order" is wrong.
- In-memory: a restart grants a fresh window, tolerable *because* it is a window.

## Store concurrency
- **`ApplyObservation(key, observation, at)`** — ingest delta, load-observe-write in one
  `BEGIN IMMEDIATE`. Exists because CAS alone loses observations under a burst (16 concurrent observers
  of one sender failed after retries).
- **`Update<T>(key, at, Func<AdaptiveProfile,T>)`** — the general form, for changes needing a *decision*
  (promotions). Matches `assess-`'s `IAdaptiveProfileStore` port exactly. **Sharp edges:** the delegate
  holds SQLite's single write lock (no I/O, no `await`, **no nested store call → deadlock**); it always
  writes so it is **not a read path**; a throw rolls back cleanly.
- **`Save`** — whole-row upsert with an **optimistic revision CAS** throwing
  `ProfileVersionConflictException` (deliberately not a `SqliteException`, so a busy-lock handler cannot
  swallow it). Token is a **revision**, not `baseline_version`, because the latter only moves on
  promotion. Stored in `adaptive_profile_revision`.
- Safe to share one instance per host: `ProfileKeyHasher`, `RobustScaleModel`, `DimensionVector`,
  `BehaviouralEvidenceEvaluator`, `SqliteAdaptiveProfileStore`.
- **Must NOT be shared:** `AdaptiveProfile` and its per-principal state.
- `SendingQuotaLedger` and `IncidentLog` carry a `Lock` — `TryReserve` is check-then-act and unlocked
  would *over-grant*.

## Known limitations (deliberate, documented in code)
- Store restore uses `RecipientHistory.DefaultCapacity/DefaultWindow`, not host `AdaptiveOptions`
  (`Load` has no options). **Decided to leave** — failure direction is safe (saturates sooner → floor
  set sooner → filter *misses* novelty rather than inventing it). Fix only if a deployment configures a
  non-default capacity.
- Regime candidate is not persisted mid-evaluation; a restart mid-candidacy restarts the candidacy.
- **Unverified claim, documented as such:** the deferred-vs-immediate transaction choice is *not*
  discriminated by any test (switching leaves everything green). SQLite's refusal to upgrade a stale
  snapshot protects the deferred version — the engine's behaviour, not ours. Do not re-mutate expecting
  a red test.

## Infra gotchas
- `dotnet` is NOT on PATH: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, `net10.0`, solution `StyloMail.slnx`.
- **Analyzers are errors** (CA1822/CA1823/CA1826/CA1861/CA1832). Write analyzer-clean code.
- Store persists `profile_key` with a `#outbound`/`#inbound` suffix (shared PK has no direction column).
- `SqliteSchema.EnsureCreated` is now genuinely idempotent; call it unconditionally.
- No async in this lane, so the SQLite async-is-synchronous trap does not apply.

## Bus hazards — read before replying to anyone
- **`reply_to_thread` archives but does NOT deliver.** Answering a peer who is waiting → `send_message`.
- **Verify at the far end by where the file actually is, never by the tool's return string.** The tell
  is the prefix: anything under `archive/` was not delivered into a live inbox.
- Lifecycle: `inbox/` = pending (reading does NOT drain); `archive/inbox/` = completed or
  undeliverable; `archive/outbox/` = `reply_to_thread` output, never delivered. Do not conclude
  "NOT DELIVERED" from `inbox/` alone — I nearly reported a false second hazard that way.
- Live prefixes: overview-, mime-, adaptive-, queue-, host-, assess-, transport-, access-.

## Process rules earned the hard way
- **A mutation must both build and change behaviour before its verdict means anything.** Three artefacts
  hit here: a mutation that stops using a field → `CA1822` → INCONCLUSIVE (never a pass); a mutation
  that is a no-op (`entries.First` read while `RemoveLast()` still runs) → TOOTHLESS while proving
  nothing; and twice a match string with wrong indentation → SKIPPED. TOOTHLESS and INCONCLUSIVE both
  mean "measured nothing".
- **A green suite is not evidence your tests still exist.** A full-file rewrite silently dropped
  `CoverageReportsHowMuchOfTheProbeWasActuallyCompared`; only a mutation check found it.
- **After removing or superseding a mechanism, re-read the comments that justified it.** Two stale
  justifications were found in this lane, one of which would have led a reader to rebuild a gate that
  had been deliberately deleted.
- **An observation is not a state.** Re-run rather than diagnose a phantom; re-read the far end rather
  than trust a return string.
- Every suppression rule produces the same *observable* outcome, so the `suppressions` list — not
  `VelocityAvailable` — is the load-bearing assertion. Never remove those reason assertions.

## Deliberately not done
- Full Mahalanobis/covariance scoring (deferred until trusted support is adequate).
- Campaign / near-duplicate grouping (spec §8) — a different component's responsibility.
- Thresholds and half-lives are **unvalidated engineering defaults**, not tuned values. They need
  representative replay data, which does not exist yet.
