# overview-, saved context

## Identity + scope
- Prefix: `overview-` (depth 0, root of the authority tree). Sole agent in fleet at start.
- Role: architect for **StyloMail**. Owns `.styloagent/spec.md`, `.styloagent/architecture.md`,
  `model-policy.yaml`, the fleet roster, and the environment control plane.
- Fleet limits: `maxFleet: 12`, `maxDepth: 3`, not paused.

## Repo state
- Root: `/Users/scottgalloway/RiderProjects/stylomail/`
- **Not a git repository yet.** No branch, no HEAD, no commits. Only `.styloagent/` scaffolding exists;
  the project itself is empty.
- Nothing has been scaffolded or implemented, deliberately gated on human approval (brainstorming
  HARD-GATE). Do not create project files until the human approves an approach.

## Charter (from `.styloagent/brief.md`)
> New project, two way mail proxy, set it up as StyloMail and await a spec

Progression: spec → shape (C4) → fleet → build the first feature.

## Status, scoping Q&A in progress
Operator decided (2026-09-22) that **overview- authors the spec**, via one-at-a-time questions,
then `.styloagent/spec.md` for sign-off before any scaffolding. **No project files created yet.**

Decisions locked so far:
- **Q1, purpose:** Mail flow in *both* directions. An SMTP gateway/relay sitting in front of
  back-end mail servers: inbound MX + outbound submission. → MTA territory.
- **Q2, why it exists:** **Security/edge boundary.** TLS + STARTTLS termination, authentication,
  rate limiting, reputation. Back-ends must never be directly reachable, everything touches us first.
- **Q3, state ownership:** **Store-and-forward.** Accept, spool to disk, return 250 immediately,
  deliver to the back-end on our own retry schedule, generate bounces on permanent failure.
  **We own the queue** → durable spool, retry/backoff, queue expiry, DSN generation. Postfix-shaped,
  not a thin router. This is the biggest complexity driver in the system.

- **Q4, back-end routing:** **Fixed mapping in config.** Domains/recipients map to back-end hosts;
  explicit, no external dependency; changes need a reload/deploy. No directory sits in the delivery
  path, so a directory outage cannot stall the queue.

Still open: wire protocol to the back-end (SMTP vs LMTP-into-store), tenancy/scale, tech stack.

## Docs
- **`.styloagent/spec.md` WRITTEN** (2026-09-22), distilled from the operator-supplied
  `email-proxy-spec.md` (repo root, draft 0.1, "Adaptive Two-Way Email Proxy"). **Awaiting operator
  sign-off.** `.styloagent/architecture.md` does not exist yet.
- The source spec is the **specification of record for implementation mechanics**; `spec.md` is the
  identity/shape layer (purpose, users, capabilities, constraints, shape). Where they disagree,
  `spec.md` states intent and the source governs mechanics.
- Source facts worth not re-deriving: transport-independent **.NET library + ASP.NET Core host + CLI**;
  **SQLite** local persistence; **Jev** is a hosted TypeSafe semantic-classification model behind a
  provider interface, **a local Jev release is NOT assumed**; weekend demonstrator = **one** MTA
  handoff *or* restricted SMTP listener, ~12 semantic dimensions, sender/recipient/relationship
  profiles with drift + velocity + acceleration, allow/hold/quarantine/defer/reject, decision ledger,
  shadow mode, deterministic replay.
- **Jev wire contract VERIFIED 2026-09-22** against the live TypeSafe docs (via the `typesafe:typesafe-ai`
  skill). Full detail in spec §6. Essentials: `POST https://api.typesafe.ai/v1/systemone`,
  `Authorization: Bearer <API_KEY>`; request `{state, model, questions}`; response `{model, answers, usage}`.
  Types: **noul** (probability only, NO confidence field), **choice** (≤255 options), **score** (2–10
  levels). Model `jev-1.13.0`; aliases `jev-latest` / `jev-preview`. 64k context (32k for state + longest
  question). Errors 401/422/429/529. Text-only, English-primary.
  **No .NET SDK exists** (Python + JS only) → the Jev adapter is an HTTP client we write.
  **No on-premise Jev**, hosted only.
  **Six discrepancies logged in spec §6.1**: no provider request id; Noul has no confidence; no documented
  timeout (1s deadline is ours); alias drift (pin `jev-1.13.0`); composite-scoring docs don't cover
  correlated outputs; confidence bands are ours to tune.
- **"Jev" also lives in the sibling repo `lucidRESUME`** (`src/lucidRESUME.AI/JevResumeDecisionProvider.cs`,
  `JevOptions.cs`, `docs/jev-parsing-experiment.md`), same pattern: deterministic first, Jev only for
  unresolved ambiguity, probability+margin gates, full audit ledger, real abstention path. That repo
  has its **own overview agent** in a separate repo, reuse requires coordination, not assumption.
- **`stylomail` has NO existing code, no git repo, no SMTP/MTA integration.** The source spec's
  instruction to "use the existing bones wherever they fit" has nothing to bind to here. This answers
  its open question §15 ("which existing SMTP/MTA integration is already present") definitively: none.
- Operator Q&A threads for Q1–Q4 all closed via `reply_to_thread` and archived (2026-09-22).

## Build state (2026-09-22)
- **`git init` done. No commits yet.** `.gitignore` written BEFORE init, verified with
  `git check-ignore -v jevkey.pvt` → matches `.gitignore:8:*.pvt`. **Never `git add .` blindly.**
- **`jevkey.pvt` (repo root, 108 bytes, `-rw-r--r--`) holds the TypeSafe/Jev API key.** Present but
  never read, never echoed. Flagged to the operator: it should ideally move out of the repo tree
  (user-secrets / env var / `~/.config`) and be `chmod 600`. **Rotate it if it is ever committed.**
- `dotnet` is at **`/usr/local/share/dotnet/dotnet` and is NOT on PATH**. Export
  `DOTNET_ROOT=/usr/local/share/dotnet` and prepend to `PATH` in every shell, or nothing builds.
  SDK **10.0.201**, ASP.NET Core runtime 10.0.5, arm64 macOS.
- Solution uses the **`.slnx`** format (`.NET 10 default`), not `.sln`. `dotnet sln StyloMail.slnx add …`.
- **Projects:** `src/StyloMail.Core` (contracts), `src/StyloMail.Persistence` (SQLite + vec0),
  `tests/StyloMail.Core.Tests`, `tests/StyloMail.Persistence.Tests`.
- **Packages:** `Microsoft.Data.Sqlite` 10.0.12, `sqlite-vec` 0.1.7-alpha.2.1 (**authored by Alex
  Garcia**, canonical `asg017/sqlite-vec`). A repackaging, `HiraokaHyperTools.sqlite-vec`, was
  evaluated and **rejected**, provenance of a native binary outweighs a minor version delta.
- **Tests: 12 passing, 0 failing.** Includes a real `vec0` load + KNN smoke test and tenant-isolation
  tests. `vec0` loads correctly on osx-arm64 (verified, not assumed).
- **Core contracts written:** `MailEnvelope`, `AuthenticationContext`, `Evidence`,
  `EvidenceAvailability`, `MailAction`, `MailAnalysisInput`, `MailAssessment`, `RecipientDisposition`,
  `SemanticDimension` (+ the 12 `SemanticDimensions.All`), `IMailAssessor`,
  `ISemanticMailClassifier`.
- **Design invariants encoded in the code**, do not undo these: Noul has no `Confidence` (it is
  nullable and documented as always-null for Noul); `EvidenceAvailability.Unavailable` is NOT a zero
  score; `MailEnvelope.UntrustedMessageIdHeader` is explicitly not an idempotency key; original MIME
  bytes are preserved separately from the analysis view; the risk index is documented as an index, not
  a probability; `MailAction` has no `Shadow` member (shadow is a mode on `AssessmentContext`).
- **Schema:** profiles (observed state + trusted baseline kept distinct), decision_ledger,
  recipient_disposition, feedback, campaign_group/member, semantic_cache, semantic_centroid +
  `semantic_centroid_vec` (vec0, tenant_scope as a filterable metadata column so isolation is enforced
  inside the vector search). `SqliteSchema.CurrentVersion = 1`.
- **`src/StyloMail.Jev/` DONE**, the HTTP adapter against the verified contract (no .NET SDK exists,
  so it is a hand-written client). Files: `JevOptions`, `JevContracts` (internal wire DTOs),
  `JevCircuitBreaker`, `JevSemanticMailClassifier`. 15 tests, all green.
  - **Pins `jev-1.13.0`, never the `jev-latest` alias** (aliases move; thresholds are tuned).
  - Error split by design: **401/422 throw `JevContractException`** (config/programming faults must be
    loud, a revoked key would otherwise look like a calm inbox); **429/529 retry with backoff**;
    **timeouts/HTTP/JSON failures degrade to explicit `Unavailable`**, never a zero score.
  - All 12 dimensions go in **one fan-out request** as `noul` questions. `ConversationContext` absent
    ⇒ continuity is `NotApplicable` and is not asked.
  - Only `FromTrustedVerifier` auth results are sent to the provider.
  - **No request/response body is ever logged.**
  - The API key is set **per request**, not on the shared `HttpClient`, and never lives in source.
- **FLEET: `mime-` is live** (runtime `claude-deepseek`, model `deepseek-flash`, effort high,
  worktree false) building `src/StyloMail.Mime`. Mission doc: `.styloagent/missions/mime-.md`.
  **`mime-` owns `src/StyloMail.Mime/` + `tests/StyloMail.Mime.Tests/`, do not edit those.**
- **TOTAL: 27 tests passing** (Core 1, Jev 15, Persistence 11).
- **TOTALS 2026-09-22 (278 passing):** Core 1, Persistence 15, Jev 15, Policy 16, Mime 87,
  Adaptive 100, Queue 44. Host + Assessment still in flight.

## COMPLETED COMPONENTS (verified by me, not just claimed)
- **Mime** (`mime-`), 87 tests. Deliberately emits observation + digest for novelty signals rather
  than fabricating a baseline comparison from one message. No network, asserted structurally.
- **Adaptive** (`adaptive-`), 100 tests. Then assigned campaign windows / near-duplicate grouping
  (evidence only, NEVER a reuse gate).
- **Queue** (`queue-`), 44 tests. Acceptance is defined by `QueueId is not null`, not a bool.
  Retention purge default 24h, **the real number is an OPERATOR decision, not a tuning parameter.**

## CORE BUGS FOUND BY AGENTS (both were mine)
1. `SpoolStore.FindOrphans` had **no age guard**, a sweep could delete a payload between its write
   and its metadata commit, manufacturing the exact unrecoverable state the ordering prevents. Fixed
   with a required cutoff from the injected clock (`queue-`).
2. `SqliteSchema.EnsureCreated` set `journal_mode = WAL` AND `synchronous = NORMAL` **inside a
   transaction**. SQLite rejects both: succeeded on first call, threw on the second → restart became
   a boot-time failure. Both pragmas must precede `BeginTransaction`. Regression tests added
   (`adaptive-` reported it, I fixed it).
**Process lesson: I wrote `QueueSchema.cs` + `SpoolStore.cs` and handed them over WITHOUT BUILDING
THEM, `SpoolStore.cs` did not even compile. Always build before handing off.**

## Fleet-wide gotcha (broadcast)
**`Microsoft.Data.Sqlite`'s `*Async` methods are synchronous under the hood**, a bare `Task.WhenAll`
serialises, so concurrency tests silently do NOT race and pass while proving nothing. Use `Task.Run`,
and mutation-test to prove a concurrency test can fail. Found by `queue-` because a broken mutation
also passed.

## OPERATING PRACTICE FOR AGENTS (tell every owner)
**Agents must build/test THEIR OWN project, never `StyloMail.slnx`.** With 6 agents in one tree the
solution build is frequently red for reasons unrelated to any given agent, someone mid-edit in
another lane. Judging your work by the solution means being blocked by other people's transient
state. **Watching the solution build is `overview-`'s job, not an owner's.**
**Agents must not yield silently.** `host-` did this three times: it hit a transient cross-lane
compile break in `mime-`'s half-written file, correctly called it transient, and stopped rather than
retrying. A silent stop is indistinguishable from a crash and gives me nothing to act on. Nudging
works but costs a round trip each time; the mission docs should say "report or continue, never yield".

## TESTING DISCIPLINE (project-wide; `mime-` proved it, 13/13 mutations caught)
**A test that cannot fail is not a test.** Three variants of the toothless-test pattern, all invisible
to a green run AND to coverage:
1. **Two code paths, one outcome**, a fast path and a slow path both ending in the same
   "rejected"/"unavailable"/"held", so a test naming one cannot prove which ran.
2. **One guard redundantly covered by another guard**, removing *either* changes nothing and both
   look tested (found by `mime-`: two independent `NotApplicable` guards; the parts gate was
   completely untested and without it the comparison ran HTML against itself, reporting a confident
   zero for a question never asked).
3. **A third-party dependency silently covering for us**, the test was green only because MimeKit
   threw on hostile input after our own validation was mutated away. **Not a property to depend on.**
**Cheapest detection order, do this BEFORE mutating:** list test names containing a *specific claim*
(`IsNotApplicable`, `BeforeParsing`, `IsUnavailable`, `IsRejected`, `IsHeld`, `IsTruncated`) and ask
**"what else could produce this same observable outcome?"** `mime-` found both round-2 cases that way
in under a minute.
**Fix shape:** make the mechanisms **distinguishable in the ledger** (suffix the reason, assert the
per-input `Reason`), not by asserting the same outcome more loudly.
**Mutation-harness traps (four, all found by agents; the hardened harness is to land at
`.styloagent/tools/mutate.py`):**
1. **A build failure is NOT a passing mutation.** Analyzers-as-errors is policy here, so `CA*`/`IDE*`
   must count as build failures, otherwise a mutation that never compiled scores as "test stayed green".
2. **Verify the restore, but source residue is only HALF the check (TRAP 4 is worse).**
3. **A missing anchor means nothing was mutated**, the harness then reports "NO TEETH", blaming the
   test instead of itself. Hard-fail on a missing anchor.
4. **TRAP 4, restoring the source is not restoring the tree.** `shutil.copy2` preserves mtime, so a
   restored file looks *older* than the binary built from the mutation, MSBuild skips the rebuild, and
   the next test run executes **the mutated binary**. This is a **FALSE POSITIVE**, a mutation scored
   "caught" by the previous mutation's stale binary, making an unguarded path look guarded. Strictly
   worse than a miss. Symptom: tests failing with values the source provably cannot produce.
   **Fix: `os.utime(path, None)` after every restore, PLUS a mandatory post-sweep green run on the
   untouched suite before believing any result above it.** My advisory's "diff for residue" was one
   layer short, it verified the source while the lie was in the binary.
5. **TRAP 5, bound every run.** An unbounded `while (true)` turns "stops making progress" into a
   **hang**, and a hang reports nothing. Bounding it changed a 3-minute hang into 14 failures in 7
   seconds. A test that cannot *finish* is the sibling of a test that cannot *fail*.
10. **TRAP 10, a doc comment is an UNTESTED ASSERTION.** Three instances in one day, three different
   owners, one shape: **documentation asserting a guarantee the code does not provide.**
   - `queue-`: `RecipientAdmission.ReEvaluateBy` documented "Required when State is Held"; the code
     applied `DefaultHoldWindow`. **`host-` read the doc, believed it, and built a dependency on a
     throw that did not exist.** Doc rot does not stay inside the file it lives in.
   - `adaptive-`: `AdaptiveProfile` claimed the store owned an optimistic version preventing races.
     It did not, *"I'd written the doc I intended the design to have, not the one it has."*
   - **Mine**: `PolicyOptions.MinimumCoverageForAllow`'s prose claimed thin evidence yields a bounded
     hold while the code returned `Allow`. Found by `assess-`.
   **Worse than a missing doc, because it is BELIEVED**, and invisible to tests, coverage and
   mutation, none of which read prose. **Remedy: pin the real behaviour with a test, THEN correct the
   prose** (what all three did), not merely reword it.
9. **TRAP 9, a negative assertion needs a POSITIVE CONTROL.** `host-`'s
   `A_resume_cannot_reach_another_tenants_sender` asserted only "nothing happened over there", which a
   **no-op resume** satisfies trivially. Fixed by also asserting the same principal id **was** affected
   in the *other* tenant's own namespace, the positive control that makes the negative claim mean
   something. Same for `Resuming_does_not_erase_the_record...`: asserting history survived is satisfied
   by a no-op; it now asserts the pause was **lifted** *and* history survived (two-sided).
   **Rule: if a test claims X did NOT happen, prove the mechanism is live by showing where it DOES.**
8. **TRAP 8, a test double MORE FORGIVING than the implementation hides defects behind a
   correct-looking test.** `host-`'s `RecordingAssessor` never read the spool and never accepted,
   which is *why* its suite stayed green over a broken seam; and its fake **invented a fallback
   idempotency key the real component deliberately does not have**, so the no-key case looked
   replay-protected while production would duplicate. **Fix: make a fake refuse exactly what the
   real component refuses**, a double must be no kinder than the thing it stands in for.
7. **TRAP 7, a test that fails only sometimes is worse than one that cannot fail.** `assess-`'s burst
   test asserted "all N observations landed"; it passed in isolation with serialisation removed and
   failed **only under full-suite load**. That teaches the team to read a real regression as
   flakiness. **Fix: assert a property the mechanism GUARANTEES and the race cannot**, e.g.
   `SaveAttempts == total && Conflicts == 0`, so it is deterministic.
6. **TRAP 6, the harness must be crash-safe; a kill corrupts the tree.** Killing a hung sweep mid-
   iteration leaves the mutation APPLIED, and **the tree looks clean**. Worse than Trap 4, because
   Trap 4 at least leaves a detectable discrepancy while this leaves nothing.
   **Fix: signal handlers restoring on SIGINT/SIGTERM, and a STARTUP REFUSAL if any `.bak` exists.**
   Found only because `queue-` scanned for EVERY mutation's signature rather than the ones it
   remembered, "the grep I'd have run by habit would have missed it." Scan exhaustively, never from
   memory.

**Shared-state rule, `mime-` corrected my wording, and the correction matters:**
> An object the host shares across threads should carry **no mutable state, instance or static**.
> Everything shared is readonly and populated at construction.
("instance" alone is one word too narrow: a parser's likeliest future mistake is a **static cache**, memoised regex, reused buffer, lazy lookup table, which an instance-field tripwire catches none of.)

**AND `readonly` IS NOT IMMUTABILITY, `adaptive-`'s addition, and it invalidates the tripwire for a
whole class of object:**
> A `readonly` field that references a **mutated** collection reads as safe on inspection while the
> contents race freely. It is the most misleading shape a shared hazard takes.
**The assertion must be two-part because there are two kinds of shared object:**
- **immutable** shared objects → reflection tripwire is correct;
- **mutable-but-shared** objects (quota ledgers, incident logs) → the tripwire is **actively wrong**,
  because they exist to be mutated and passing a field-shape check implies a guarantee it does not
  provide. Only a **contention test** proves the lock works. `adaptive-` removed such an object from
  its tripwire list for exactly this reason, a tripwire that passes on an unsafe object is worse
  than none.
**Consequence found in practice:** `SendingQuotaLedger.TryReserve` was check-then-act, so contention
**over-granted**, and the quota is the only bound on how much a late detection lets escape. A
security control failing open, in the one direction that matters.

## SESSION HANDOFF NOTE (written at ~90% context)
**State:** ~615 tests across 11 projects. **All components built, tested and mutation-audited
except `AccessProxy`.** The store-and-forward system assesses real mail end to end once `ingress-`
finishes the Host wiring (4 items, see `.styloagent/missions/ingress-.md`).
**Live fleet:** `ingress-` (Host wiring), `access-` (IMAP/POP3/SMTP proxy), `adaptive-` (quota
window), `queue-`+`transport-` (cross-lane seam test), `assess-`/`mime-`/`host-` idle+complete.
**Cross-lane decisions still settling:** (a) Integration.Tests project STANDS with `queue-` owning
assertions and `transport-` owning the rig, **STANDS because it was already built**; my (b)
revision was withdrawn as moot.
**Deliberately open, do not assume:** host- delete-after-accept spool deletion (two questions with
`queue-`: shared spool root? measured peak?); the `SmtpIngressSink` sink MUST NOT call `AcceptAsync`
(the assessor is the only acceptor, this trap is the highest-cost one in the repo).
**Method that worked, worth continuing:** one agent per component with a mission doc; agents raise
cross-lane friction rather than patching it; **verify claims independently rather than accepting
reports**, that found 3 defects in my own code today; run the thing rather than reading it back;
mutation-test every safety claim and report when a mutation fails to discriminate.

**TRAP 13, "flaky" and "deterministic" are both unreliable readings on a live tree (`assess-`).**
A thread produced **four wrong claims, all the same error with the sign flipped**: "deterministic" (3 runs),
"cleared" (1 run), "9-in-10 flaky" (10 runs, on a tree mid-refactor), "88/88 green" (1 run). Every one was
measured in good faith and arithmetically correct, **they measured a tree state, not a defect.** With eight
agents editing one tree, a flake report is actionable ONLY with (a) **the tree state it was measured on** and
(b) **a reproduction rate on a QUIESCED tree**. Without both, the next person re-derives a transient and calls
it a defect. Note my own "812 tests green" and "717 passed, 1 failed" two turns apart are the same error.
**Also: an experiment with zero events in both arms is INCONCLUSIVE, not negative**, "with no events there is
nothing to compare." The honest phrasing is **"not reproducible as of now, hypothesis untested"**, which is
more useful than "fixed". Proper method (from `assess-`): runsettings not file edits, interleaved arms so tree
drift cannot land on one, and a **manipulation check** to prove the setting applied.

**MY OWN ERRORS to avoid repeating:**
1. I misquoted a test name (`...MayBeSharing` vs actual `...MayBeShared`) *while* asserting these
   reports are the audit record. **Quote symbol names from a fresh read, never from memory.** Also:
   never inline a running test count into a message, it is stale by the time anyone reads it.
2. **I read `MailAssessor.cs:787` as still using `BuildAssessmentId` and was about to report a fix as
   not landed. It HAD landed, between my two greps.** With 8 agents editing concurrently, **a single
   read is a snapshot, not a state.** Before reporting a defect in another lane, re-read, and prefer
   asking the owner (`read_agent`) over asserting from one grep. I made exactly the mistake I have
   been warning agents about.
3. Similarly, a test run showing failures may just mean the owner is mid-edit, I saw 5 failures and
   then 83/83 minutes later. **Re-run before reporting a regression in someone else's lane.**
**SQLite concurrency:** `Task.Run` is necessary but NOT sufficient. Parallel calls contend for the
single write lock; `SQLITE_BUSY` from inside the code under test is a **lock-policy** problem, not a
sync bug. Do not "fix" it with retries, that masks contention behind a green test. Leave
`BusyTimeout` at the driver default (30s).

## CONCURRENCY RULE, my advice was wrong, corrected by `assess-` (2026-09-22)
I told `adaptive-` to add CAS to `SqliteAdaptiveProfileStore.Save` and told `assess-` it could then
**drop its call-site serialisation**. That was wrong. `assess-` did it, tested against the real store,
and put the serialisation back.
> **Optimistic concurrency protects correctness; it does not protect throughput, and a bounded retry
> converts lost throughput into lost work.**
Only one writer wins a round; a bounded budget means the other N-1 fail outright. Measured: 16
concurrent observers of one profile → `ProfileUpdateConflictException` after 4 attempts.
**Why it matters more here than elsewhere:** *"a burst is many messages for one sender, that is
exactly the compromised-account shape StyloMail exists to catch. **The hot profile is the interesting
profile.**"* Contention is the design case, not an edge case.
**Rule:** serialise where two writers can contend on one key as a NORMAL event; keep CAS underneath
for writers the serialisation cannot see (another process). The CAS stays, it is a second layer.

## ARCHITECTURAL DECISION: the assessor is the ONLY component that accepts (2026-09-22)
Found by `host-` as a **duplicate-mail defect**. `MailAssessor` called `AcceptAsync` with
`IdempotencyKey = assessmentId`; `SubmissionsEndpoints` called it with the client's `Idempotency-Key`
header. Two acceptances, two keys, no dedupe → **the message queues twice**. Masked at the time only
because the Host sent `spool://pending`, which passes `RequireDurable` but names no file, so the
assessor read null bytes and deferred, i.e. **the obvious fix (spool properly) would have turned a
latent bug into a live one.**
**Decision (follows spec §4 step 7, acceptance is inside the pipeline; `AssessmentContext.AssessmentOnly`
exists so the assessor knows whether to accept):**
- **The assessor keeps accepting. The Host does NOT touch the queue on the submission path.**
- The Host calls `AssessAsync(AssessmentOnly = false, ClientIdempotencyKey = <header>)` and reads the id.
**Core additions made for this:**
- `AssessmentContext.ClientIdempotencyKey`, **the queue key must be the CALLER's key, never a minted
  id.** `assessmentId` is fresh per attempt, so a retrying client created a new queue entry every time
  and client replay was silently broken end to end.
- `MailAssessment.SubmissionId` (nullable), the durable queue id; **null means "we did not take
  this"** (assessment-only, Defer, Reject), not a missing value.
**Second-order:** `host-`'s replay fast-path was **dead code its tests wrongly certified as working**, the lookup could never match. Its surface test stayed green even if the fast-path were deleted,
because the *queue's* own dedup returned the same id. Only `A_retry_does_not_spend_a_second_assessment`
had teeth. Classic trap-3: the test asserted a property it did not own.

## Ownership decisions recorded
- **Idempotency keys belong to the Queue**, not the Host, only the queue can enforce it
  transactionally (partial unique index). `host-` consumes it.
- **No DSN/bounce generation in the queue**, permanent failures recorded, left to the upstream MTA.
- **`Authentication-Results` detail convention set in Core**: colon-separated `key=value`; `dkim` has
  `d=`/`s=` from the VERIFIER, never the message header. Alignment is deliberately NOT computed in
  `AuthenticationResult`, observation and judgement stay in separate layers.
- **Spawn parameters that work** (operator-corrected 2026-09-22): `runtime: claude-deepseek`,
  `model: deepseek-flash`. A `claude`/`sonnet` pair silently **exits with 0 tokens**, it does not
  error visibly, so always confirm via `fleet_status()` after spawning.

## ROLE, operator-corrected twice on 2026-09-22
> "You should have agents do it" · "You keep the overall shape and decisions"

**`overview-` does NOT implement. Agents implement.** My job: own `spec.md`, `architecture.md`,
`model-policy.yaml`, the fleet roster, cross-cutting decisions, integration, and arbitration. I built
Core/Jev/Policy myself before the correction landed; those now sit with me as owner until named
owners exist. **Do not write feature code again, spawn an owner.**

## CURRENT FLEET + OPEN ITEMS (as of the last overview turn)

| Owner | State | Open work |
| --- | --- | --- |
| `overview-` |, | spec/architecture/coordination; **context nearing its limit, see handoff note** |
| `mime-` | complete 91 | DKIM alignment offered, not taken |
| `adaptive-` | complete 126 | **implementing the SenderQuotaLedger rolling window** (500/hr, unvalidated; persistence deliberately out of scope) |
| `queue-` | complete 87 | **owns the cross-lane worker↔port integration test** in a NEW `tests/StyloMail.Integration.Tests` |
| `host-` | **stopped on budget, 94 green** | handed over cleanly; checkpoint at `saved-context/host--context.md` |
| `ingress-` | **live (new)** | the 4 remaining Host wiring items, see `.styloagent/missions/ingress-.md` |
| `transport-` | complete 168 | correcting the ingress sketch (it would reintroduce double-accept) |
| `assess-` | complete 93 | consumer of the quota ledger; told what "remaining" will mean |
| `access-` | live | IMAP/POP3/SMTP proxy; credential seam first, retrieval before submission |

**Cross-lane work in flight:** queue- ↔ transport- on the integration test (covers `InDoubt`,
partial per-recipient results, a throwing port, "the two agree by inspection, not by execution").
Canvas rule issued: **build your own project to unblock yourself, build the SOLUTION before declaring
done** (queue- broke transport-'s build and did not know).

**Known unverified mechanism (documented, do not re-mutate expecting red):** `adaptive-`'s
`ApplyObservation` `BEGIN IMMEDIATE` is NOT discriminated by any test; SQLite refusing to upgrade a
stale snapshot is what protects the deferred form.

## Fleet roster (original spawn set, max 12)
| Prefix | Owns | Colour |
| --- | --- | --- |
| `overview-` | Core, spec, architecture, Jev adapter, Policy engine | `#A1887F` |
| `mime-` | MIME adapter, deterministic evidence | `#80CBC4` |
| `adaptive-` | Profiles, drift/velocity/acceleration, trusted learning | `#90A4AE` |
| `queue-` | Durable queue, spool, crash recovery | `#FFB74D` |
| `host-` | ASP.NET Core host, CLI, operator surface | (unassigned) |
| `transport-` | Transport + provider connectors | **NOT SPAWNED, blocked on operator decision** |

All spawned with `worktree: false` (shared repo), their file scopes are disjoint by project
directory. Mission docs: `.styloagent/missions/<prefix>.md`.

## Provider integration (spec §8, added 2026-09-22, NOT BUILT)
Two operator questions outstanding:
1. **Mailchimp is a marketing platform and the source spec puts "marketing platform" out of scope.**
   Either lift the exclusion, or the real target is **Mandrill** (transactional, different API).
2. **Gmail/Outlook = mailbox access** (OAuth + Google/Microsoft app verification), which exceeds the
   source spec's "no mailbox hosting, IMAP client, behind an established MTA" posture.
**Credential model decision:** pass-through where a client can present creds (SMTP submission);
stored OAuth only where push/webhook makes it unavoidable. **Credential mode is a connector property,
never a Core property**, the default deployment must hold ZERO secrets.
**Recommended first adapter: Cloudflare Email Routing** (inbound-only, no OAuth, no stored creds).

## Domain research done (don't redo)
Canonical two-way mail proxy architecture = **credential-aware, protocol-terminating proxy tier** in
front of mail back-ends: read the account from credentials the client already presents (SASL PLAIN,
OAUTHBEARER, XOAUTH2, HTTP Basic/Bearer), look up destination (LDAP / SQL / Redis / flat file), replay
auth to the backend, bridge the session both directions. Real client identity carried forward via
PROXY protocol, `XCLIENT`, or IMAP `ID`.

The design space forks on three axes:
1. **Transport**, SMTP relay (inbound MX + outbound submission) vs IMAP/POP access vs both.
2. **Transparency**, transparent TCP pass-through with per-connection static routing (nginx mail
   module, `auth_http` returning `Auth-Server`/`Auth-Port`; Perdition) vs **stateful user→backend
   affinity** (Dovecot Director, exists to stop index corruption on shared storage; being phased out,
   successor is Dovecot's commercial cluster) vs protocol-terminating relay (Stalwart proxy, Oracle MMP).
3. **Purpose**, load distribution, TLS offload, migration, or filtering/audit/archiving.

Comparable systems worth naming if the human asks: nginx mail module, Perdition, Dovecot Director,
Stalwart `proxy` (multi-protocol migration proxy), Oracle Messaging Multiplexor, HAProxy (IP-affinity
only, cannot pin a *user* across different client IPs).

## Hard rules
- Production forbidden unless the operator says `prod` / `promote to prod` in that turn.
- Never print or persist secret values, reference where a credential lives, never the value.
- No external DNS / Cloudflare / tunnel / secret-store changes without a documented entrypoint AND
  explicit authority.
- Deployment is not complete until the canonical staging URL passes real Playwright; an IP-only smoke
  test never counts.
- Report to the human via `ask_operator` for decisions; `send_message` is for agent-to-agent.
- Mine is the flagship model; spawned specialists step one tier down (see `model-policy.yaml`).

## Infra notes
- `.styloagent/environments/policy.yaml`, `controlOwner: overview-`. No environments registered yet.
- Browser governance unconfigured (`browser/` empty); no Playwright origin allow-listed.
- `channel/` empty apart from this file; no other agents have ever run here.
