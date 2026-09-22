# `queue-`, durable delivery queue, spool and crash recovery

## Identity and scope

Owner of `src/StyloMail.Queue/` and `tests/StyloMail.Queue.Tests/`. **Nothing else.** Do not edit
Core, Persistence, Jev, Mime, Adaptive, Policy, Host, `spec.md` or `email-proxy-spec.md`; if a Core
contract must change, message `overview-`.

Never send mail from this component. Delivery is out of scope, this owns durability and state.

**Never read, print or reference `jevkey.pvt`.** Do not run `git add` or `git commit`.

## OPEN WORK (assigned by overview-)

**Worker↔port integration test**, mine, covering gaps (2) and (3): run the real `SmtpDeliveryPort`
against `QueueDeliveryWorker`, so `IDeliveryPort` carries a real SMTP result and not only my fakes.
Scenarios: (1) in-doubt doesn't settle; (2) partial delivery must not read as full success;
(3) a throwing port; (4) cancellation, **now settled by transport-**: their port classifies per
recipient and always returns, `InDoubt` only where the terminator was already written.

**DONE, `tests/StyloMail.Transport.Tests/DeliveryWorkerSeamTests.cs`** (written by me, reviewed by
transport-). **2 pass + 1 skipped open question.** Their suite 174 green, mine 88.

The location flip-flopped three times: `overview-` ruled (a) a new project, revised to (b), then
re-revised to (a) as "built so moot", then finally **"let (b) stand, do not revert again"**. Messages
crossed with messages-cum-deletes. **(b) is the standing decision and `overview-` has since exited.**
`tests/StyloMail.Integration.Tests` does NOT exist and must NOT be recreated, I built it, removed it,
and transport-'s `InternalsVisibleTo("StyloMail.Integration.Tests")` is now dangling (harmless).
Do NOT write a second loopback SMTP server, their `Support/FakeSmtpServer` is the rig.

**RESOLVED, and by neither of my two hypotheses.** transport- found that `DropDuringDataBody`
wasn't dropping during the body: the fake replied to DATA and closed without reading, so the outcome
depended on whether the client's write completed into the kernel socket buffer. ≤4 KB → terminator
written → `InDoubt` (honestly); ≥64 KB → the write itself fails → plain temporary failure. **Both
outcomes are correct**, there was no contradiction, only a fixture whose name promised an
interruption it could not deliver. Knob renamed `CloseAfterDataCommand`; both sides pinned in their
`SmtpSessionTests`. My test was **removed rather than weakened**, per its own remarks.

**Scenario 4 DONE**, `A_cancellation_landing_after_the_terminator_surfaces_as_in_doubt`. **~400ms, not
10s:** `WaitForMessagesAsync(1)` is a real sync point (messages are added before `FinalReplyDelay`), so
the 30s delay is a *ceiling never waited out* and the drain window closes inside it. Mutation-verified
(passing the cancelled token to `CompleteAsync` reddens it). Keep it fast, this suite is the one with
no clock dependence.

## `ListAsync` paging cursor — FIXED (found by ingress-)

**The cursor took its timestamp from the PROBE row and its id from the last KEPT row.** Ordering is
`created_at DESC`, so the probe's timestamp is older — every row between the two was skipped on the
next page, silently, while the listing reported itself complete. A console paging a queue got two of
three messages, no error. Rows are now held as `(QueueId, CreatedAt)` **pairs**; both halves come from
one row.

**The reason the existing test could not see it is the important part.** `Paging_visits_every_item_
exactly_once` gives every item the *same* `created_at` — deliberately, to exercise the `queue_id`
tiebreaker — and with equal timestamps the probe's `created_at` and the kept row's are **identical**,
so the mispairing is invisible. **The two halves must differ for the defect to appear.** Proven by
re-introducing the defect: old test PASSES, new test FAILS.

`Paging_visits_every_item_exactly_once_with_distinct_timestamps` advances the clock between accepts.
Guarded by mutation **Y**. **Do not merge the two paging tests** — they cover opposite halves
(tiebreaker vs distinct ordering), and the one that hid this bug is the one that looks redundant.

**After changing code a mutation anchors to, re-run that mutation** — M went INVALID when this fix
removed its anchor line.

## MailFrom / null sender, direction-dependent (DO NOT RE-REVERSE)

**`MailEnvelope.MailFrom` is `""` for a null sender** (`<>` is wire notation, normalised at the parse
boundary). **Inbound accepts a null sender**, a DSN delivered to a user is ordinary mail.
**Outbound returns `QueueAdmission.RefusedNullSender`** (value 7, announced to host-, who maps
admissions), never a throw, so a declined message is distinguishable from a caller construction
error and never leaves `AssessAsync` unhandled.

The bug this replaced: `Require(submission.MailFrom)` ran **unconditionally**, throwing on `""` before
`IsNullSender` was consulted, so **inbound DSNs were refused outright, i.e. mail loss**, and it
predated the null-sender check. A comment of mine claimed "inbound is unaffected", transcribed from a
ruling that was itself wrong. **A ruling that says a case is "unaffected" is a claim to check, not a
fact to transcribe**, and a reassuring comment is the most dangerous kind, because it stops the next
reader looking.

**The predicate lives in Core: `StyloMail.Core.SenderAddresses.IsNullSender`.** Do NOT reintroduce a
local copy, the two that existed had already diverged within the hour (`<>` vs `< >`), and the
consolidation is what made that visible. Core is **exact**: `""`, whitespace, and `<>` exactly.
`< >` is NOT a null sender (malformed address, not the null sender) and currently falls through to be
treated as an ordinary address, an address-syntax gap, recorded not fixed.

Rule: **policy refusals return a `QueueAcceptResult`; construction errors throw.** A policy refusal
put in `ValidateSubmission` won't even compile, that's the shape telling you where it belongs.

**Three-place nullable change** (see below): making a field nullable needs contract + bind + **read**.
The compiler catches two, `int` → `int?` is implicit, so `GetInt32` on a NULL column builds clean and
throws at runtime. `transport-` found exactly that at `QueueStore.cs:1732`.

## `HopCount`, RESOLVED (nullable everywhere), schema v4

Was: `MaxHops` read a constant 0, **a guard whose condition cannot be false**, found by `transport-`.
No test in this lane could see it, because the tests *construct* their own count (correct mechanism,
never fed). That is the class to watch for: **correct and unfed survives every verification.**

Now: `MailEnvelope.HopCount`, `QueueSubmission.HopCount` and `QueueItem.HopCount` are all `int?`, and
`hop_count` is a **nullable column**. **`null` means "not observed", never encode it as 0.** Zero is a
real observation (arrived with no prior hops); null is "nobody looked". Collapsing them is how a
backstop reads as enforced while never firing.

- `null` → submission **proceeds, unenforced**, and the null in the row IS the report.
- `EnforceHopLimit` is unaffected: `NULL >= $maxHops` is never true.
- **Making a field nullable is a THREE-place change**, contract, bind, and *read*. The compiler
  catches two: `int` → `int?` is implicit, so `GetInt32` on a NULL column compiles and throws at
  runtime. Only a test exercising the null read path catches it.
- **Schema v4**, a v3 database has `hop_count NOT NULL` and must be deleted.
- Still inert until `ingress-` populates the envelope and `assess-` copies it in `Step7Async`; but
  now visibly unenforced rather than disguised as a zero.

## Sweep locking, the convention (overview- ruling)

**Any harness that mutates source takes `.styloagent/tools/.mutation-sweep.lock`**, committed or
ad-hoc, in `.styloagent/tools/` or `/tmp`. One line:
`.styloagent/tools/sweep-lock.sh with <your-command>` (acquires, releases on success/failure/Ctrl-C/
SIGTERM; refuses a second holder; same lock file as `mutate.py`). `mutate.py` takes it too **even
though isolation makes it unnecessary**, so "is the lock held?" is a *complete* answer.

Rationale (do not lose it): a lane ran mutation rounds from `/tmp/assess-mutation-round*.sh`, **invisible to anyone auditing for sweep tools**, so a bystander cannot check for a tool they cannot
see. The lock is how a harness announces itself.

## Mutation sweep, final

`python3 .styloagent/tools/mutate.py` → **44 CLAIMED across both lanes** (24 queue + 20 mime), 0 GAP,
0 ELSEWHERE, 0 INCONCLUSIVE, 0 INVALID. Harness parses TRX, not console text (console parsing cannot
see parameterised `[Theory]` failures and produced false `ELSEWHERE`). Console-vs-TRX cross-check
reports INCONCLUSIVE on disagreement, **that check is the part to keep**.

Separately owned elsewhere: `host-` hosts the worker in-process as a hosted service (gaps 1 and 3).

### Do NOT do this (settled, do not re-litigate)
- **Do not record blanket `InDoubt` on drain-cancel.** transport- was right: ambiguity depends on how
  far the protocol got, which only the port knows. A duplicate-risk flag that fires on cancellations
  is one you learn to ignore; it must keep its weight in the retry/terminal-reason paths.
- The worker applies a result that arrives after cancellation (`CancellationToken.None` on
  `CompleteAsync`), pinned by test + mutation X.

## Current state, COMPLETE, green (including the delivery worker)

- Branch: `main` (shared tree, not a worktree, nothing to `wrap_up`).
- **`QueueSchema.CurrentVersion = 3`.** `EnsureCreated` throws on mismatch instead of running with
  wrong columns. A v1/v2 dev database must be deleted; the error says so.
- Tests: **61 passed / 0 failed**, 0 build warnings.

## The delivery worker, BUILT

`QueueDeliveryWorker` + `IDeliveryPort`. **My mission doc says "delivery is out of scope"; the
architecture assigns the worker to `queue-` and says it "never opens a socket".** I flagged the
discrepancy to `overview-`; the doc may still be stale, so trust the architecture + this file.

Design decisions worth not re-litigating:
- **One item at a time per worker**; concurrency = run several workers with distinct `WorkerId`.
- **Backoff/scheduling is NOT the worker's job**, the store owns it. A worker that scheduled its
  own retries would be a second, divergent copy of that policy.
- **Drain is bounded** (`DrainTimeout`): shutdown stops claiming but an in-flight delivery gets its
  window, because cutting it off risks a duplicate the upstream already accepted. Past the window
  the delivery is cancelled and the **lease is left to expire**, no outcome is invented.
- **A port that throws → `TemporaryFailure`** with a detail saying the outcome is unverified.
- **A missing payload → `PayloadMissing`, lease deliberately NOT completed.** Settling it would
  erase the only signal that accepted mail has gone missing.

## Shared mutation harness

`.styloagent/tools/mutate.py`, read its header before touching it. **One file per lane**, discovered
at runtime; never a shared list (a lane rewriting a shared list can silently drop another's entries
and produce a false all-clear).

```
python3 .styloagent/tools/mutate.py            # every lane
python3 .styloagent/tools/mutate.py queue      # one lane
python3 .styloagent/tools/mutate.py queue BC   # selected mutations
```

Queue's 23 mutations (A–W) live in `.styloagent/tools/mutations/queue.py`, each naming the test that
**claims** its behaviour. **Verdicts are three-way** (from `mime-`): CLAIMED / ELSEWHERE / GAP, a full-suite run otherwise reports "caught" when some *other* test caught it, which is this
advisory's own failure mode inside the tool that detects it. **All 23 are CLAIMED.** When you add a
mutation, name its claiming test, and if you ever add a new verdict branch, deliberately fire it
once, an unexercised branch is the exact thing this harness exists to find.

After changing production code that a mutation anchors to, **re-run that mutation**: two of mine
went INVALID (anchor gone) because my own fix deleted the line they targeted.
- Build env: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
  (SDK 10.0.201, `net10.0`).
- Verify: `dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj`

## Public surface (asked for by `host-`, keep in step with them)

```
AcceptAsync(QueueSubmission, ct) -> QueueAcceptResult     // .IsAccepted => QueueId is not null
ClaimNextAsync(workerId, tenantId?, ct) -> QueueLease?     // .PendingRecipients = attempt exactly these
CompleteAsync(lease, DeliveryReport, ct) -> QueueCompletionResult
RecoverAsync(tenantId?, ct) -> QueueRecoveryReport         // periodic sweep + after restart
ResolveHoldAsync(queueId, HoldResolution, decidedBy, tenantId?, ct) -> bool
ResolveQuarantineAsync(queueId, QuarantineResolution, decidedBy, tenantId?, ct) -> bool
GetItemAsync(queueId, tenantId?, ct) -> QueueItem?         // tenant-scoped
GetAttemptsAsync(queueId, tenantId?, ct) -> [QueueAttempt] // tenant-scoped, oldest first
FindSubmissionAsync(tenantId, idempotencyKey, ct) -> SubmissionLookup?
CountByStateAsync(tenantId?, ct) -> {DeliveryState -> int}
Backoff(queueId, attempt) -> TimeSpan                      // pure; for "when will this retry"
OpenPayload(QueueItem) -> Stream                           // throws QueueIntegrityException if gone
```

**Delivery port** (`IDeliveryPort.cs`): `IDeliveryPort.DeliverAsync(DeliveryRequest, ct) ->
DeliveryPortResult`, with `DeliveryPortResult.AsReport(workerId)`. `DeliveryRequest` carries the
payload **bytes**, never a spool reference, the port must never learn `spool://`.
**`DeliveryPortContract.ReportableOutcomes`** is the closed set a port may return:
`Delivered · TemporaryFailure · PermanentFailure · InDoubt · HopLimitExceeded`. Everything else is
queue-owned and is rejected at runtime; `QueueStore.ValidateReport` reads that same set, and a test
asserts the documented and enforced sets are identical for every enum value, keep it that way.

`QueueSubmission` carries: TenantId, InternalMessageId, Direction, TrustedPrincipalId, MailFrom,
MimeDigest, Payload (bytes), Recipients[] (`RecipientAdmission{Recipient, State, ReEvaluateBy}`),
UntrustedMessageIdHeader?, IdempotencyKey?, HopCount, ExpiresAt?.

**The queue mints its own queue id and payload reference**, a caller cannot supply either. A
caller-chosen reference could name a payload that was never durably written.

## The invariants that must not regress

1. **Payload durably spooled before the metadata row commits.** Crash between ⇒ orphan payload
   (sweepable), never metadata referencing a missing payload.
2. **Acceptance is defined by `QueueAcceptResult.QueueId`**, not a boolean. Spool failure *throws*
   `SpoolUnavailableException`; it never returns a result.
3. **Delete the payload whenever the result does not name the queue id we just spooled**, covers
   admission refusal *and* losing an idempotency race. (`IsAccepted` is the wrong test: a duplicate
   is an acceptance belonging to the *winner's* bytes.)
4. **Lease ownership, not elapsed time, decides whether a report applies.** Expiry only lets the
   recovery sweep take work from a presumed-dead worker.
5. **Attempt history is append-only.** Results are recorded even when not applied. History holds
   delivery outcomes *and* queue/policy events (`HoldExpired`, `HoldResolved`, `QuarantineReleased`,
   `QuarantineRejected`, `LeaseExpired`, `Expired`, `HopLimitExceeded`). Workers may not report those.
6. **An expired hold is a policy event.** Recipient stays `Held`; only `ResolveHoldAsync` changes it.
   Quarantine has *no* deadline, only `ResolveQuarantineAsync` moves it.
7. **Reads and resolutions are tenant-scoped** when a `tenantId` is passed; cross-tenant reads as
   absent, not as an error.

## Things a fresh me must not re-derive

- `dotnet` is NOT on PATH. Export both vars (above).
- **Analyzer diagnostics are errors in this repo** (CA1822 etc.). Write analyzer-clean code.
- Microsoft.Data.Sqlite has **no `SqliteTransactionMode`**, use `BeginTransaction(deferred: false)`.
  Its `*Async` methods are **synchronous under the hood**: a bare `Task.WhenAll` serialises. Use
  `Task.Run` to get a real race in tests.
- `LibraryImport` requires `AllowUnsafeBlocks` assembly-wide, `SpoolStore` uses `DllImport` with a
  scoped `SYSLIB1054` suppression. Don't "modernise" it.
- **Don't undercut `BusyTimeout`.** `SqliteCommand.CommandTimeout` defaults to 30s and drives the
  busy timeout. I once set 15s without checking, an unforced halving of the lock-wait window.
  Under real concurrency the symptom is `SQLITE_BUSY` from inside the code under test, which reads
  like a bug and isn't: it's lock policy. Default is now 30s, matching the driver on purpose.
- `PRAGMA journal_mode` must be set **before** `BeginTransaction` (SQLite rejects it inside one).
  My `QueueSchema.EnsureCreated` does this correctly at the top; don't move it. Repeat-safety is
  exercised by `QueueHarness.Reopen`, which creates a second store over the same database.
- Queue DB may be shared with Persistence; the queue sets `synchronous=FULL` per connection (its
  commit *is* the acceptance point).
- **macOS caveat:** directory `fsync` is not a hard barrier on APFS (needs `F_FULLFSYNC`). Real on
  Linux. Documented in `SpoolStore.FlushDirectory`, not hidden.
- **Mutation-test before believing a green suite.** Two bugs in this component were found only
  because a mutation survived: the idempotency-race payload leak, and the lease-ownership rule.
  A passing test that doesn't fail under mutation is not evidence.
- **SWEEPS ARE NOW ISOLATED, `overview-` ruled it, and it is done.** `mutate.py` copies the tree to a
  private temp dir and mutates/builds/tests **there**; the shared tree is never touched. Verified with
  the experiment that demonstrated the bug: suite concurrent with a full sweep went from
  **1,1,2,1,2 failures → 0,0,0,0,0,0**, full sweep 24 CLAIMED. `git worktree` is better and **blocked,   no baseline commit**; migrate if one is ever authorised. The lock + `.bak` check survive as the
  detector for a sweep violating its own isolation, *not* as the mitigation. Do NOT go back to
  in-place mutation: it makes the fleet completion gate lie for every lane.
- **The two-signal check is now in `.styloagent/PROTOCOL.md` under `## Completion gate`**, placed
  there by me because `access-` and `transport-` independently showed the sweeper's own notes are
  exactly where a bystander never looks. `overview-` exited, so I decided and **wrote the reasoning
  into the note** (reversible, not silent). Do not move it back into tool docs.
- **CONFIRMED CAUSE, with an exact reproduction:** holding the lock gives **12/12 green (30 consecutive
  clean runs total)**; applying mutation `I` by hand gives **`Failed: 27, Passed: 61`**, character-
  for-character `access-`'s catastrophic run. Every failure they saw was my sweep. **The suite is
  deterministic.** Before disbelieving any red in a lane I touched, check **both signals**:
  `ls .styloagent/tools/.mutation-sweep.lock` (running now) **and** `find src -name '*.bak'` (killed
  sweep, mutation still applied, **the SIGKILL case leaves no lock**, found by `transport-`).
- **A sweep mutates the SHARED source tree. Tell everyone before you start one.** `access-` correctly
  reported my suite as flaky (4 runs, 4 different results, including a clean pass), the cause was my
  sweep editing `src/StyloMail.Queue/*.cs` in place while they ran `dotnet test`. Reproduced: suite
  alone = 10/10 green; concurrent with a sweep = 1,1,2,1,2 failures. There is now a lock at
  `.styloagent/tools/.mutation-sweep.lock` (refuses a second sweep, and explains itself to whoever
  finds it). **Residual limitation, NOT solved: a concurrent run is still affected**, the real fix is
  a worktree per sweep. If your suite looks flaky, check for that lock file before believing it.
- **A mechanism built to catch a class of error is itself subject to that class.** The harness's
  `[FAIL]` regex could not match a parameterised `[Theory]` line (parameters contain spaces), so
  failed-name extraction returned empty while the count was non-zero → **false `ELSEWHERE`**. Now
  parses TRX XML, plus a console-vs-TRX cross-check that reports INCONCLUSIVE on disagreement.
  **My lane has no theories, so my CLAIMED verdicts were right by luck.** When you add a new verdict
  or helper to a verifier, test the verifier.
- **`reply_to_thread` ARCHIVES; it does not DELIVER.** My reply to `transport-` was written to
  `archive/outbox/` and never appeared in `inbox/`, so two agents waited hours on answers that
  were "sent". For anyone actively waiting, use **`send_message`**. Verify at the far end, `find
  .styloagent/channel -name "<recipient>-*"`, not just that the call reported `sent →`.
- **"My lane is green" ≠ "I broke nobody".** `StyloMail.Transport` references `StyloMail.Queue`, so a
  transient compile error of mine blocked transport-'s test run before I noticed. **After touching a
  shared project, build `StyloMail.slnx`, not just your csproj.** The dependency graph makes your red
  someone else's red.
- **A doc comment is a claim about code, not evidence about it** (from `host-`, who was misled by
  one of mine). Audit normative docs, `must`/`never`/`always`/`only`/`bounded`, by asking *what
  would prove this false?* Two of mine were untestable as written: a "clamped" claim tested below
  the ceiling, and a "counts terminal payloads too" claim tested only on non-terminal rows.
- **A mutation harness needs four rules, all learned the hard way.** (1) `os.utime` after restore,   `shutil.copy2` preserves the *source* mtime, so a restored file looks older than the binary built
  from the mutation, MSBuild skips the rebuild, and the next run silently executes the MUTATED
  binary. (2) A per-run timeout, a mutation that hangs must report INCONCLUSIVE, not block.
  (3) Startup refusal if a `.bak` exists, being killed mid-iteration leaves a mutation applied,
  and that tree *looks* clean. (4) A mandatory post-sweep green run, or a mutation can be credited
  as "caught" by the previous mutation's lingering binary.
- **No unbounded loop in a test.** `while (true)` converts "stops making progress" into a *hang*,
  which blocks the run and reports nothing. Bound every drain/retry loop and throw on exceeding it.
- **Every test name is a claim, check it against what the test can observe.** `Assert.Empty(files)`
  cannot distinguish "refused before spooling" from "spooled then cleaned up"; assert the
  distinguishing observable (here: the tenant spool dir was never created) or rename the test.
  Same for `TerminalFailure`: assert *which* bound was hit (via `LastError`), not just that it is
  terminal, exhaustion, lifetime expiry, hop limit and permanent failure all land there.

## Deliberately not done

- No delivery worker / MTA integration / bounce composition (out of scope by charter).
- No `IAsyncEnumerable` streaming; reads are per-item.
- Retention purge ships (`TerminalPayloadRetention`, default 24h), the window is an operator
  decision, not an engineering default to trust (spec §11).

## Known friction for `overview-`

- `RecoverAsync` is O(queue size), it must know every referenced payload to find unreferenced ones.
  Correct for a periodic sweep, wrong for a hot path. Documented on the method.
- A crash-looping worker is bounded by message lifetime, not the per-recipient attempt counter: we
  never observed an attempt, so counting one against the recipient would be inventing evidence.
- `delivery-quality`: `queue_attempt.worker_id` holds *the actor*, a delivery worker, or the
  reviewer/policy principal that recorded a decision.

## Report history

- 2026-09-22, queue build complete, 44 tests green; reported to `overview-`.
- 2026-09-22, `host-` flagged three gaps in the surface (tenant-scoped read, quarantine release with
  audit, idempotency lookup); all three shipped, plus `RequireDurable` per `overview-` and the
  dropped-`UntrustedMessageIdHeader` defect I found myself. Schema v3, 53 tests green. Replied to
  `host-`, updated `overview-`.
