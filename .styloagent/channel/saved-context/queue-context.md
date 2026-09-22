# `queue-` — durable delivery queue, spool, crash recovery, delivery worker

## Identity and scope

Owner of `src/StyloMail.Queue/` and `tests/StyloMail.Queue.Tests/`. **Nothing else.** Do not edit Core,
Persistence, Jev, Mime, Adaptive, Policy, Host, or the spec docs; message the owner instead.

**Never send mail from this component.** It owns durability and state. The delivery *worker* is mine
(the architecture assigns it) but it never opens a socket — it dispatches through `IDeliveryPort`.

**Never read, print or reference `jevkey.pvt`.** Do not run `git add` or `git commit`.

Build: `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
(SDK 10.0.201, `net10.0`). `dotnet` is NOT on PATH.

## Current state — COMPLETE and green

- Tests: **Queue 97 · Host 168 · Transport 192 · Assessment 124 · solution 0 errors.**
- **25/25 mutations CLAIMED**, no gaps. Tree clean, no sweep lock, no stray `.bak`.
- **`QueueSchema.CurrentVersion = 4`.** `EnsureCreated` throws on mismatch; **a v3 dev database must
  be deleted** (v4 made `hop_count` nullable).

## Public surface

```
AcceptAsync(QueueSubmission, ct) -> QueueAcceptResult     // .IsAccepted => QueueId is not null
ClaimNextAsync(workerId, tenantId?, ct) -> QueueLease?     // .PendingRecipients = attempt exactly these
CompleteAsync(lease, DeliveryReport, ct) -> QueueCompletionResult
RecoverAsync(tenantId?, ct) -> QueueRecoveryReport         // periodic sweep + after restart
ResolveHoldAsync(queueId, HoldResolution, decidedBy, tenantId?, ct) -> bool
ResolveQuarantineAsync(queueId, QuarantineResolution, decidedBy, tenantId?, ct) -> bool
ListAsync(QueueListingQuery, ct) -> QueueListingPage       // keyset-paged; TenantId REQUIRED
GetItemAsync(queueId, tenantId?, ct) -> QueueItem?
GetAttemptsAsync(queueId, tenantId?, ct) -> [QueueAttempt]
FindSubmissionAsync(tenantId, idempotencyKey, ct) -> SubmissionLookup?
CountByStateAsync(tenantId?, ct) / Backoff(queueId, attempt) / OpenPayload(QueueItem)
```

`IDeliveryPort.DeliverAsync(DeliveryRequest, ct) -> DeliveryPortResult`, with
`DeliveryPortResult.AsReport(workerId)`. Requests carry payload **bytes**, never a spool reference.
**`DeliveryPortContract.ReportableOutcomes`** is the closed set a port may return:
`Delivered · TemporaryFailure · PermanentFailure · InDoubt · HopLimitExceeded`. `ValidateReport`
reads that same set and a test asserts the documented and enforced sets match for every enum value.

## Invariants that must not regress

1. **Payload durably spooled BEFORE the metadata row commits.** A crash between leaves an orphan
   payload (sweepable), never metadata referencing a missing payload.
2. **Acceptance is defined by `QueueAcceptResult.QueueId`**, not a boolean. Spool failure *throws*
   `SpoolUnavailableException` and never returns a result.
3. **Delete the payload whenever the result does not name the queue id just spooled** — covers
   admission refusal *and* losing an idempotency race. (`IsAccepted` is the wrong test: a duplicate
   is an acceptance belonging to the *winner's* bytes.)
4. **Lease ownership, not elapsed time, decides whether a report applies.** Expiry only lets recovery
   take work from a presumed-dead worker.
5. **Attempt history is append-only.** Recorded even when not applied.
6. **An expired hold is a policy event, never an acknowledgement.** Recipient stays `Held`; only
   `ResolveHoldAsync` moves it. Quarantine has no deadline — only `ResolveQuarantineAsync`.
7. **Policy refusals return a `QueueAcceptResult`; construction errors throw.** A policy refusal put
   in `ValidateSubmission` will not even compile — that is the shape telling you where it belongs.
8. **Reads and resolutions are tenant-scoped** when a `tenantId` is passed; cross-tenant reads as
   absent, not as an error. `ListAsync` *requires* a tenant.

## Settled — do NOT re-litigate

- **`MailFrom` null sender is direction-dependent.** `MailEnvelope.MailFrom` is `""` for a null
  sender. **Inbound accepts it** (a DSN to a user is ordinary mail — refusing it was real mail loss);
  **outbound returns `QueueAdmission.RefusedNullSender`**, never a throw.
  The predicate lives in **`Core.SenderAddresses.IsNullSender`** — do not reintroduce a local copy;
  the two that existed diverged within the hour. Core is exact: `""`, whitespace, `<>` exactly.
  `< >` is NOT a null sender and currently falls through as an ordinary address (address-syntax gap,
  recorded not fixed).
- **`HopCount` is `int?` everywhere; `null` means "not observed", never 0.** Zero is a real
  observation. `null` proceeds *unenforced* and the null in the row IS the report. `EnforceHopLimit`
  is unaffected (`NULL >= x` is never true). Still inert until `ingress-` populates the envelope and
  `assess-` copies it in `Step7Async`.
- **Do NOT record blanket `InDoubt` on drain-cancel.** Ambiguity depends on how far the protocol got,
  which only the port knows. A duplicate-risk flag that fires on cancellations is one you learn to
  ignore.
- **The seam test lives in `tests/StyloMail.Transport.Tests/DeliveryWorkerSeamTests.cs`** (my file,
  their project). `tests/StyloMail.Integration.Tests` does NOT exist and must NOT be recreated.
  Do NOT write a second loopback SMTP server — their `Support/FakeSmtpServer` is the rig.
- **Do not merge the two paging tests.** They cover opposite halves (queue_id tiebreaker vs distinct
  ordering), and the one that hid a real bug is the one that looks redundant.

## Things a fresh me must not re-derive

- **Analyzer diagnostics are errors here** (CA1822 etc.). Write analyzer-clean code.
- Microsoft.Data.Sqlite has **no `SqliteTransactionMode`** — use `BeginTransaction(deferred: false)`.
  `CancelAfter` has **no TimeProvider overload** (the overload is on the *constructor*); use
  `TimeProvider.CreateTimer` when the timer must start later.
- `LibraryImport` needs `AllowUnsafeBlocks` assembly-wide — `SpoolStore` uses `DllImport` with a
  scoped `SYSLIB1054` suppression. Don't "modernise" it.
- **Don't undercut `BusyTimeout`** — `SqliteCommand.CommandTimeout` defaults to 30s and drives it.
- **`reply_to_thread` ARCHIVES; it does not DELIVER.** Use `send_message` for anyone waiting.
  Verify at the far end — `find .styloagent/channel -name "<recipient>-*"` — not just that the call
  said `sent →`. This stranded two agents for hours.
- **Build `StyloMail.slnx`, not just your csproj, before declaring done.** `Transport` references
  `Queue`, so your red is someone else's red.
- **A doc comment is a claim about code, not evidence about it** (from `host-`, misled by one of
  mine). Audit normative docs by asking *what would prove this false?*
- **A guard whose condition cannot be false** is the recurring defect — seen at three levels (test
  assertions, verdict extraction, the harness's own self-check). **Ask what input makes a guard fail;
  if you can't name one, it is decoration.** Corollary: **correct-and-unfed survives every
  verification** — `MaxHops` never fired and no test in this lane could see it.
- **A mechanism built to catch a class of error is itself subject to that class.** A verifier is not
  exempt from being verified.
- **A defect that shows up only where two lanes differ is invisible from inside either one.** Every
  serious defect found in this lane was found by someone whose context differed.
- **Intermittency discriminator:** a *real* flake is systematic (same test, same property, stable
  under paired A/B). A false one varies with an *input*.
- **"I don't have room" is a claim to verify**, not to defend. I twice declined work on that basis
  and it was a four-call job.

## Mutation harness

`.styloagent/tools/mutate.py` + `.styloagent/tools/mutations/<lane>.py`. **Sweeps run in an isolated
copy** — never in place, because the completion gate is fleet-wide and an in-place sweep makes it lie
for every lane. `git worktree` is better and **blocked: no baseline commit**.

**Any harness that mutates source takes the lock:** `.styloagent/tools/sweep-lock.sh with <cmd>`.
Two signals, **both** needed — `ls .styloagent/tools/.mutation-sweep.lock` (running now) **and**
`find src -name '*.bak'` (killed sweep, mutation still applied). The lock is absent in the SIGKILL
case, which is the one that matters most. Both checks are in `.styloagent/PROTOCOL.md`.

**After changing code a mutation anchors to, re-run that mutation** — mine went INVALID twice that way.

## Known limitations (honest, not solved)

- `RecoverAsync` is O(queue size) — it must know every referenced payload to find unreferenced ones.
- A crash-looping worker is bounded by message lifetime, not the per-recipient counter (we never
  observed an attempt, so none can honestly be counted).
- The sweep still mutates a *copy*, but a concurrent run is affected while it lives; isolation is per
  sweep, not per file.
- Retention purge ships; the window is an operator decision, not an engineering default to trust.
- **No host starts the worker yet** — `host-` owns that (in-process hosted service).

## Fleet adjacency

- `transport-` — the delivery port and its rig; owns `SmtpDeliveryPort`, `FakeSmtpServer`.
- `host-` — HTTP/Host surface, hosts the worker, maps `QueueAdmission` (incl. `RefusedNullSender`).
- `assess-` — builds `QueueSubmission` in `Step7Async`; owns `AssessmentValidation`.
- `ingress-` — SMTP ingress, populates the envelope (`MailFrom = ""`, `HopCount`).
- `overview-` — architect/arbiter; owns Core and the spec.
