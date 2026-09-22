# `adaptive-`, profiles, behavioural evidence and trusted learning

## Your scope
Own `src/StyloMail.Adaptive/` and `tests/StyloMail.Adaptive.Tests/`. Nothing else.
Do **not** modify `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
`src/StyloMail.Mime/`, `.styloagent/spec.md`, or `email-proxy-spec.md`. Other agents own those.
If you believe a Core contract must change, `send_message` to `overview-` instead of editing it.

You may **read** `src/StyloMail.Persistence/SqliteSchema.cs`, the `profiles`,
`semantic_centroid` and `semantic_centroid_vec` tables already exist and are yours to use. Implement
your store inside `src/StyloMail.Adaptive/` using the existing `SqliteConnectionFactory` and
`SemanticCentroidStore`; do not add or change files in the Persistence project.

## Read first
- `.styloagent/spec.md`, project spec. §5 "Shape of the problem" is the pipeline; §6 covers evidence.
- `email-proxy-spec.md` (repo root), **§7 "Adaptive profiles and temporal behaviour" is your detailed
  brief**. §4 steps 3 and 5 describe where you sit in the pipeline. This is the specification of record.
- `src/StyloMail.Core/`, `Evidence`, `EvidenceAvailability`, `EvidenceOrigin`, `MailAnalysisInput`,
  `MailDirection`, `RecipientDisposition` (for its `RecipientScopedSignalIds`).
- `src/StyloMail.Persistence/`, `SqliteSchema` (the `profiles` table shape), `SqliteConnectionFactory`,
  `SemanticCentroidStore`.

## What to build
1. **Bounded profile scopes**: tenant + traffic class, outbound authenticated sender, inbound sender
   identity with authentication provenance, recipient, and sender-recipient relationship. Domain-level
   context is a fallback and is **never equivalent to account trust**. Keep inbound and outbound
   statistics distinct while permitting explicit relationship linkage.
2. **Two evidence stores that must never be conflated**:
   - *Observed state*, all attempts and recent campaigns, **including rejected traffic**. Detects abuse, bounds throughput.
   - *Trusted baseline*, approved training samples only. A sent or unreported message is **not** automatically trusted.
3. **Robust scoring**: start with robust standardized distances and diagonal variance; clamp scales with
   variance floors. Full covariance/Mahalanobis is a later option once sample support is adequate.
   **Missing dimensions are masked with coverage recorded, never filled with zero.**
4. **Temporal evidence**: fixed clock buckets (**not** "one tick per message"), multiple windows for
   burst vs slow change, count/rate features normalized by elapsed time. Semantic buckets require
   sufficient samples or stay missing. For a smoothed normalized vector `z` at bucket `t`:
   `velocity[t] = (z[t] - z[t-1]) / elapsed_time`,
   `acceleration[t] = (velocity[t] - velocity[t-1]) / elapsed_time`.
   Emit a **reason-shaped** description ("recipient fan-out rising while payment-redirection evidence
   also rises"), never a bare acceleration scalar.
   **Suppress derivative evidence** during cold start, long gaps, sparse buckets, or incompatible
   schema/regime changes, and record bucket uncertainty and minimum-support checks.
5. **Learning controls**: time-aware EWMA `alpha = 1 - exp(-elapsed/tau)` with bounded per-event and
   per-principal contribution. Cap how fast trusted baselines move. **Freeze baseline promotion during
   suspected compromise** while continuing observed-traffic accounting. Keep label provenance,
   corrections, and checkpoint/rebuild. New regimes start as candidates and are promoted only with
   enough trusted support plus a stability check. Baseline rollback must **not** restore depleted
   sending quotas or erase incident history.

## Hard constraints
1. **Acceleration can never alone justify a hard block.** It is noisy evidence. Your output is evidence;
   only deterministic policy selects an action. You produce `Evidence`, never `MailAction`.
2. **Absence is not zero.** Unavailable or insufficiently-supported dimensions must be masked and
   reported through `EvidenceAvailability`, never coerced into a baseline comparison.
3. **Tenant isolation is absolute.** Never read one tenant's profile when serving another. The centroid
   store already filters tenant inside the vector search, rely on that, do not re-implement it.
4. **Profile eviction must not grant a fresh quota.** Dehydrating state is not a reset.
5. Use injected `TimeProvider` everywhere. No `DateTimeOffset.UtcNow` in logic, replay depends on a
   fixed clock. Your tests must use a controllable clock.

## Tests
xUnit with a controllable clock. Cover at minimum: cold start (no false acceleration from missing
buckets), sparse buckets, long time gaps, a legitimate scheduled burst distinguished from novel
fan-out by traffic class, slow baseline poisoning (unlabelled/suspicious events must affect observed
state but **not** silently become trusted history), baseline freeze during suspected compromise,
regime change suppressing derivative evidence, EWMA bounds, and tenant isolation.

## Build notes (important)
- `dotnet` is NOT on PATH. Every shell needs:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, TargetFramework `net10.0`. Solution file is `StyloMail.slnx`:
  `dotnet sln StyloMail.slnx add <path>`. **Another agent may be editing the .slnx concurrently**,   if your add fails, retry once, and still verify your own project builds directly with
  `dotnet build <your csproj>`.
- ProjectReference `StyloMail.Core` and `StyloMail.Persistence`.
- Do not run `git add` or `git commit`.
- **Never read, print, or reference `jevkey.pvt`.**

## Done when
`dotnet test` passes for your project and you have seen it green. Report to `overview-` via
`send_message`: files created, test count, contract friction, and anything deliberately not done.