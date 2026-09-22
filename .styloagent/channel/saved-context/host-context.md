# `host-`, saved context (authoritative)

> Supersedes `saved-context/host--context.md`, which now carries a SUPERSEDED banner and is
> kept for history only.
> Written 2026-09-22 at the operator's request, immediately before standing by.

## Identity + scope

`host-` owns the ASP.NET Core HTTP host, CLI and operator surface for StyloMail.

- **I own:** `src/StyloMail.Host/**`, `tests/StyloMail.Host.Tests/**`.
- **Never edit:** `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue,Assessment,Transport}/**`,
  `.styloagent/spec.md`, `email-proxy-spec.md`. **Never read or reference `jevkey.pvt`.**
- Mission doc: `.styloagent/missions/host-.md`. **Extended 2026-09-22** by `overview-` to include
  the SMTP/Cloudflare ingress wiring (see "Work in flight" below).

## Current state, verified, not assumed

| Check | Result |
| --- | --- |
| `dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj` | **94/94 green** |
| `dotnet build StyloMail.slnx` | **Build succeeded** |
| Committed | **Nothing**, `git add`/`git commit` forbidden by my mission |

```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
dotnet build StyloMail.slnx          # REQUIRED before claiming done, see fleet rule below
dotnet run --project src/StyloMail.Host -- serve | assess <f.eml> | replay <dir> | quarantine … | profiles …
```

## Routes and the privilege each requires

| Route | Policy |
| --- | --- |
| `POST /v1/assessments` | `Assess` |
| `POST /v1/submissions` | `Send`, **`Idempotency-Key` header now REQUIRED, missing → 400** |
| `GET /v1/submissions/{id}` | `Send` |
| `GET /v1/decisions/{id}` | `Review` |
| `POST /v1/feedback` | `Feedback` |
| `POST /v1/quarantine/{id}/release` | `Review` |
| `POST /v1/controls/senders/{id}/pause` | `Administer` |
| `POST /v1/controls/senders/{id}/resume` | `Administer` |
| `POST /v1/session` | anonymous; **only mapped when browser channel enabled** |
| `/health/live`, `/health/ready`, `/metrics` | anonymous, must carry no message content |

CLI: `serve`, `assess` (`--semantic` opt-in), `replay`, `quarantine list|release`, `profiles inspect`.

## Work in flight, READ THIS BEFORE TOUCHING THE PROJECT

**A second agent is editing `src/StyloMail.Host/Hosting/`.** Files that appeared during my session
and are **not mine**: `HostIngressSink.cs`, `HostTransportOptions.cs`, `IngressComposition.cs`,
`IngressHostedServices.cs`, `PrincipalSubmissionAuthenticator.cs`. `HostAuthOptions.cs` also gained
`ApprovedSenderIdentities` (SMTP `MAIL FROM` allow-list) from that agent.

**Do not revert or duplicate that work.** At the time of writing it is correct on the point that
matters most: `HostIngressSink` does **not** call `QueueStore.AcceptAsync`, it calls
`AssessAsync` with `AssessmentOnly = false` and lets the assessor accept, then maps
queue id → `IngressDecision.Accepted(queueId)`, else `Reject`/`Defer`. That is the durability rule
honoured and the double-accept trap avoided. `Program.cs:41` resolves `ISmtpIngressSink` at startup
for fail-fast, matching the assessor pattern.

Coordinate before editing `Hosting/`, it is a shared surface now.

## Decisions of record (do not re-litigate)

1. **Only the assessor accepts.** Spec §4 step 7. The host must never call `AcceptAsync` on the
   submission path. Getting this wrong produced two accepts under two different idempotency keys,    one message, two deliveries.
2. **Registering the pipeline is the host's job; building it is `assess-`'s** (`overview-`'s
   ruling, "do it, do not ask again").
3. **Credentials are environment variables only**, `TYPESAFE_API_KEY` (use
   `JevOptions.ApiKeyEnvironmentVariable`, never the literal) and `STYLOMAIL_PROFILE_KEY`
   (`HostCredentials.ProfileKeyEnvironmentVariable`). `jevkey.pvt` is explicitly **not** a source.
   Unconfigured → refusing sentinel; half-configured or master key < 32 bytes → refuse to start.
4. **Cross-tenant reads return 404, identical to a nonexistent id**, no existence oracle.
5. **Reading the decision ledger requires `Review`.** A sender cannot read its own decision.
6. **Shadow mode requires `Administer`**; a sender requesting it gets 403, not silent ignoring.
7. **Two decision ledgers exist** (`host_decision_ledger` vs Persistence's `decision_ledger`).
   Renamed to `host_*` to stop a real startup-breaking collision. Which is canonical is still
   `overview-`'s call.
8. **`/v1/submissions` requires `Idempotency-Key`.** The MTA/Cloudflare ingress paths are
   deliberately exempt, no client key exists there by construction.

## Open items

- **THE FLAKE WAS NOT MINE, ROOT CAUSE FOUND (2026-09-22). Do not "fix" code that was
  never wrong; that is the real risk here.**

  **Cause:** `.styloagent/tools/mutate.py` rewrites `src/StyloMail.Queue/*.cs` **in place in the
  shared working tree** (read → `.bak` → `write_text`, no isolation). While a sweep runs, any
  agent building or testing against Queue gets failures that are real, reproducible, and caused
  by a file on disk rather than by any code path. `StyloMail.Host` references `StyloMail.Queue`,
  and every failure recorded in this lane was in a test that calls into Queue, which is the
  varying-victim signature we read as "flaky" and spent two hours on. It was never flaky.

  **Corroboration in my own evidence:** `mutate.py` has mtime **07:24**, inside the 07:15–07:25
  window `assess-` identified as when my failures occurred.

  **ALWAYS RUN THIS BEFORE BELIEVING A FAILURE**, cheaper than the debugging it replaces:
  ```
  ls .styloagent/tools/.mutation-sweep.lock      # present => a sweep is running
  find src -name '*.bak'                         # residue => a sweep was interrupted
  ```
  Both absent ⇒ the tree is clean and the failure is real. Verified clean at 07:35, with my suite
  at **127/127 green**.

  **What is NOT claimed:** not that no Host flake exists, 8 clean runs bounds a rate, it does not
  zero one. If the flake returns on a *verified-clean* tree, revisit: the parallel/serial
  experiment at ~100 runs per arm, capturing `Error Message` not just names.

  **Practice (from `assess-`, agreed):** a flake report is only actionable with **the tree state
  it was measured on** and **a rate measured on a quiesced tree**. This whole thread produced four
  wrong claims from sampling a tree that eight agents were editing plus a mutation sweep.

- **Spool efficiency, do not start without answering two questions first.** The host spools the
  payload so the pipeline can read it back; the queue then writes its own copy. `overview-`
  ruled: delete the host's copy on **every** exit path, never before a confirmed accept; **ask
  `queue-` whether our spool roots are shared** (their orphan sweeper needs an age cutoff) and
  **measure the peak rather than assuming** the cost. Both questions are still unanswered, this
  is why nothing was deleted.
- **Assessor wiring is done and verified**; the ingress adapter is being built by the other agent.

## Corrections I made to the record (keep these; they are the audit trail)

- I told `queue-` that `RecipientAdmission.ReEvaluateBy` being "required when Held" meant a null
  deadline would **throw**. **False.** It is `DateTimeOffset?`; null resolves to
  `QueueOptions.DefaultHoldWindow`. I reasoned from an XML comment that was itself wrong while
  their code was available to read. They fixed the comment and pinned the behaviour with a test.
- I told `assess-` their 85/85 might not exercise the seam end to end. Correct when sent, but it
  crossed with my fix, the fake had already been changed to read the spool and accept.
- I twice found the tree unable to build and reported it only to `overview-`, not to the owning
  agents (`queue-`, `assess-`). Both were their own mid-edit state, but I held evidence they did
  not. Tell the owner as well as `overview-` next time.

## Hard-won lessons (the reason these files are worth reading)

- **A doc comment is a claim about code, not evidence about it.** When a constraint is load-bearing,
  read the implementation. This is what caught `spool://pending` silently passing `RequireDurable`.
- **A test that cannot fail is not a test.** Three separate defects in this lane were invisible to
  green runs: the replay fast-path (deleting it left the surface test green because the *queue's*
  dedup produced the same id and the same "Duplicate" status); two resume tests that passed against
  a resume doing **nothing**; and a fake assessor that never accepted, which is why a broken seam
  looked green. **Mutation-test the claims your tests make in their names.**
- **A negative assertion needs a positive control.** "Acme's sender is untouched" is satisfied by a
  no-op. Assert the positive in the same test, that the same principal id *was* affected in the
  acting tenant's own namespace.
- **A fake must not be kinder than production.** Mine invented a fallback idempotency key where
  `MailAssessor` deliberately has none, so the no-key case looked replay-protected in tests while
  production would duplicate. It also omitted `Submission`, leaving the new field untested.
- **Verify by running, not by reading back.** Defects found this way and no other: the lazy-singleton
  assessor meant a half-configured deployment **booted healthy** and would have failed on first
  mail; a 422-vs-500 probe proved DI actually constructed `MailAssessor`; the mutation showed my own
  replay-test "fix" was cosmetic.
- **`CREATE TABLE IF NOT EXISTS` is a no-op on an existing table** → a new column produces a startup
  failure *caused by deploying*, visible only on upgrade. `HostDatabase.ApplyColumnMigrations`
  handles this additively.
- **Fleet rule:** build **your own project** to unblock yourself, but build **`StyloMail.slnx`**
  before declaring done. Your project green is not the claim "nothing I did broke anyone".

## Infra gotchas

- **`WebApplicationFactory` passes its own argv to the entry point**, the CLI parser must treat a
  leading `--option` as host configuration, not a command, or the test host never builds.
- **Anti-forgery tokens are bound to the authenticated claims identity.** `SignInAsync` does not
  change `context.User`; minting a token while the request still looks anonymous makes every later
  legitimate request fail validation. Set `context.User` first.
- **Minimal hosting reads configuration before `ConfigureWebHost` layers test config in.** Never
  decide scheme registration from `configuration.GetSection(...)` at startup, bind options and
  resolve per request.
- **`QueueSchema` is owned by `QueueStore.InitializeAsync`.** Do not call it directly (it moved from
  v1 to v3 and gained a `TimeProvider` mid-build).
- **`Microsoft.Data.Sqlite` `*Async` methods are synchronous**, concurrency tests need `Task.Run`,
  and leave the busy-timeout at the driver default (30s).
- Config keys: `StyloMail:Storage:{SpoolRoot,DatabasePath}`,
  `StyloMail:Auth:Principals:<n>:{PrincipalId,Key,TenantId,Privileges:<n>,ApprovedSenderIdentities:<n>}`,
  `StyloMail:Auth:EnableBrowserCookieChannel`, `StyloMail:Auth:CookieLifetime`.
- **Secrets** live only in env vars / a configured store. Nothing sensitive is in the repo.
  `jevkey.pvt` is `overview-`'s and is not mine to read.

## Bus hazard

`reply_to_thread` **archives without delivering**. Three agents were stranded by it in one session,
including me, replies that existed but never arrived. **Use `send_message`.** If a reply seems
missing, check `archive/outbox/` before assuming silence.
