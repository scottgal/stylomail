# `queue-` — durable queue, spool and crash recovery

## Your scope
Own `src/StyloMail.Queue/` and `tests/StyloMail.Queue.Tests/`. Nothing else.
Do **not** modify `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
`src/StyloMail.Mime/`, `src/StyloMail.Adaptive/`, `src/StyloMail.Policy/`, `.styloagent/spec.md`,
or `email-proxy-spec.md`. Other agents own those. If a Core contract must change, `send_message`
to `overview-` instead of editing it.

**Two files already exist and are a starting point, not a finished design** — review them
critically and change whatever is wrong:
- `src/StyloMail.Queue/QueueSchema.cs` — `queue_item`, `queue_recipient`, `queue_attempt` tables.
- `src/StyloMail.Queue/SpoolStore.cs` — atomic write-temp-fsync-rename spool.
The project and its test project are already scaffolded and added to `StyloMail.slnx`.

## Read first
- `.styloagent/spec.md` — §5 "Shape of the problem" (the durability boundary), §4 constraints.
- `email-proxy-spec.md` (repo root) — **§10 "Mail transport correctness" is your detailed brief**;
  §11 covers storage and failure modes. This is the specification of record.
- `src/StyloMail.Core/` — `MailDirection`, `RecipientDisposition` (note `DeliveryState`), `MailAction`.
- `src/StyloMail.Persistence/` — `SqliteConnectionFactory` to reuse.

## The single most important property
**An SMTP `250` after `DATA` transfers delivery responsibility.** So:
- Either the original payload, routing and queue metadata are durably persisted **before**
  acceptance, or the message is deferred without acceptance.
- **Disk full or unavailable durable storage must never yield a successful acceptance.**
- Payload is spooled and flushed **before** the metadata row that references it is committed. That
  ordering means a crash can only leave an orphan payload (sweepable) — never metadata pointing at
  a payload that does not exist (unrecoverable loss). Preserve this property.

## What to build
1. `QueueStore` — accept (durable, ordered as above), claim by lease, complete, fail, and recover.
2. **Lease-based claiming with recovery.** A worker claims an item with a time-bounded lease. A
   lease that outlives its worker must be reclaimable, so a crash mid-delivery never strands a
   message forever.
3. **Per-recipient state.** Multi-recipient messages persist an individual disposition per
   recipient and **retry only the recipients still pending**. Never reject an entire SMTP
   transaction while silently keeping a subset.
4. **Bounded retry** with exponential backoff and a configured expiry; on expiry the item becomes
   `TerminalFailure`. Handle permanent failures through the upstream MTA's DSN policy — **never
   send bespoke warnings or bounces to an unverified, possibly spoofed `From` address.**
5. **Loop detection and hop limits** on `queue_item.hop_count`.
6. **Admission control**: bound queue count and bytes per tenant so one tenant cannot exhaust the
   spool and deny service to others.
7. **Retry ambiguity is surfaced, not hidden.** SMTP delivery is not exactly-once: if the upstream
   accepted a message but the acknowledgement was lost, a retry may duplicate it. Preserve the
   attempt history in `queue_attempt` and **do not pretend `Message-ID` deduplication solves this.**
8. An expired hold is a **policy decision, not a delivery acknowledgement** — do not conflate them.

## Hard constraints
- `DeliveryState` lives in Core; use it, do not fork your own enum.
- No `DateTimeOffset.UtcNow` in logic — inject `TimeProvider` so crash/lease/expiry behaviour is
  testable against a controllable clock. Your tests must use one.
- **You DO own the delivery worker** (see `.styloagent/architecture.md`), but **it must never open a
  socket.** It leases accepted items, dispatches per recipient, schedules bounded retry and drains
  gracefully — speaking to the outside world **only through `IDeliveryPort`**, which is yours to
  define and `transport-`'s to implement. The queue must not learn SMTP.
  <br/>*(Corrected 2026-09-22: this line previously read "Never send mail from this component.
  Delivery is out of scope", which contradicted the architecture and would have led a fresh agent to
  refuse work that is theirs. Found by `queue-`.)*
- Do not run `git add` or `git commit`. **Never read, print, or reference `jevkey.pvt`.**

## Tests
xUnit, controllable clock. Cover at minimum:
- acceptance is refused when the spool cannot write (simulate with an unwritable path) — **no `250`**;
- a crash between payload write and metadata commit leaves a sweepable orphan and **never** metadata
  referencing a missing payload;
- a lease held by a dead worker is reclaimed after expiry;
- a multi-recipient message retries only the pending recipient after a partial success;
- retry backoff is bounded and the item ends in `TerminalFailure` at expiry;
- hop limit / loop detection trips;
- per-tenant queue bounds are enforced;
- attempt history is append-only across retries.

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201, TargetFramework `net10.0`. **Other agents may edit `StyloMail.slnx` concurrently** —
  if an add fails, retry once; always verify your own project builds via
  `dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj`.

## Done when
`dotnet test tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj` is green and you have seen it.
Report to `overview-` via `send_message`: files created, test count, what you changed in the two
pre-existing files and why, contract friction, and anything deliberately not done.