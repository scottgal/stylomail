**From:** queue-
**Timestamp:** 2026-09-22T05:40:49.9823750+01:00
**Priority:** normal

# queue- complete: durable queue + spool, 44 tests green

DONE. `dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj` → 44 passed / 0 failed, stable over 4 runs, 0 build warnings. Scope respected: only `src/StyloMail.Queue/` and `tests/StyloMail.Queue.Tests/` touched.

FILES
src/StyloMail.Queue/: QueueOptions.cs, QueueContracts.cs (new); QueueSchema.cs, SpoolStore.cs (rewritten); QueueStore.cs (new, the main surface).
tests/: QueueTestHarness.cs (TestClock + fixture), QueueAcceptanceTests, QueueLeaseAndRecoveryTests, QueueRecipientAndRetryTests, QueueAdmissionAndHoldTests.

TWO PRE-EXISTING FILES — CHANGED, AND WHY
1. `SpoolStore.cs` did not compile. CA1822 (`ReferenceFor` static-izable) is an *error* in this repo (I confirmed analyzer diagnostics are errors here; no Directory.Build.props — the SDK promotes them). Worth knowing: the scaffolding was committed without ever being built.
2. `SpoolStore.FindOrphans` was **unsound and I consider this a real defect**: it deleted every unreferenced payload with no age guard. Between the payload write and the metadata commit, an *in-flight* acceptance is indistinguishable from a true orphan — a sweep would delete a payload microseconds before its metadata committed, manufacturing exactly the unrecoverable state the ordering exists to prevent. Now takes a required cutoff, supplied from the injected clock; a fresh orphan is deliberately *not* swept (test pins this).
3. Spool writes now directory-fsync after the rename. A flushed file does not make its own directory entry durable, so a power loss could leave SQLite's committed row pointing at a name that never reached disk. Fail-closed except EINVAL/ENOTSUP. (macOS/APFS caveat documented honestly: needs F_FULLFSYNC, so real on Linux, partial on macOS.)
4. `QueueSchema` was missing what admission control and recovery need: `payload_bytes` (byte budget without a directory walk), `idempotency_key` + partial unique index, per-recipient `next_attempt_at`, `re_evaluate_by`/`hold_surfaced_at`, `purged_at`, `trusted_principal_id`, and a schema version guard that **throws** on mismatch instead of silently running with missing columns. Now v2.

TWO DESIGN DECISIONS WORTH YOUR REVIEW
- **Acceptance is defined by the queue id**, not a boolean: `IsAccepted => QueueId is not null`, so no future edit can report success without the durable row that success is a claim about.
- **Lease ownership, not elapsed time, decides whether a late report applies.** I found this by mutation testing: my first mutation didn't fail the test, which showed the rule was doing work the test wasn't checking. Reasoning now explicit in-code — the expiry is a *liveness heuristic* letting recovery take work from a presumed-dead worker, not a rule about who owns the result. A slow-but-alive worker's report is applied (discarding it guarantees a duplicate); once recovery actually reclaims, the report becomes history-only.

BUG FOUND AND FIXED BY WRITING A CONCURRENCY TEST
Cleanup after admission refusal tested `if (!result.IsAccepted)` — but a *duplicate* from a lost idempotency race **is** an acceptance, belonging to the winner's bytes. So every lost race leaked a payload permanently. Now keyed on "the result doesn't name the queue id I just spooled". Verified: mutation reverts to the old check → 3/3 failures; correct code → 3/3 passes.

Also: Microsoft.Data.Sqlite's `*Async` are synchronous under the hood, so a bare `Task.WhenAll` serialises and the race test wasn't racing at all. Fixed with `Task.Run`. Worth knowing fleet-wide if others are writing concurrency tests.

TEST COVERAGE — all 8 mandated scenarios, plus 36 more
Refused-acceptance-on-unwritable-spool (no 250, no row); crash-between-commits ⇒ sweepable orphan + zero missing-payload faults; dead-worker lease reclaimed (and the inverse: no reclaim while alive); multi-recipient partial retry naming only the pending recipient; bounded+monotonic+capped backoff ending in TerminalFailure, and expiry-by-lifetime; hop limit trips at accept, at config-change, and on stored items; per-tenant count and byte bounds with cross-tenant isolation; append-only attempt history asserted as prefix-preserving snapshots. Mutation-tested three safety-critical behaviours (orphan cutoff, reclaim sweep, compensating delete) — all caught.

CONTRACT FRICTION / DECISIONS I MADE, NOT YOU
- **Idempotency key storage is in my schema** (spec §10 says HTTP 202 needs it and the queue is the only component that can enforce it transactionally). Flagging: if the host agent planned to own that table, we should reconcile.
- **No DSN/bounce generation** — permanent failures are recorded (`PermanentFailure`) and left to the upstream MTA's policy, per the never-bounce-to-an-unverified-From rule.
- **Retention purge ships** (`TerminalPayloadRetention`, default 24h) because without it the per-tenant byte bound would eventually refuse everyone. The window is a configurable engineering default; the operator owns the real number.
- `DeliveryState` used from Core, never forked. Item-level state is a roll-up; the *nuance* lives in a derived, non-stored `QueueItemOutcome` so `PartiallyDelivered` can't be collapsed into a success or a total failure.
- Crash-loops are bounded by message lifetime, not the per-recipient counter: we never observed an attempt, so counting one against the recipient would be inventing evidence.

BLOCKER FOR YOU, NOT FOR ME
`dotnet build StyloMail.slnx` currently fails with 13 errors, all in `src/StyloMail.Mime/` — `EvidenceAttribute` no longer has a 2-arg constructor and `BoundedMimeMessageAnalyzer` still passes `KeyValuePair<string,string>`. Looks like Core changed under the Mime agent mid-flight. My project and tests build green in isolation. Not my lane, so I have not touched it — but it will block a whole-solution build and any `wrap_up` that runs solution tests.

DELIBERATELY NOT DONE: no delivery worker, no MTA/SMTP connection, no bounce composition (all by charter). No `IAsyncEnumerable` streaming reads. `RecoverAsync` is O(queue size) by nature of orphan detection — documented on the method as a periodic sweep, not a hot path.
