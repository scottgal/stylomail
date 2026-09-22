> **SUPERSEDED 2026-09-22.** The authoritative `host-` checkpoint is now
> **`saved-context/host-context.md`**. Read that one.
>
> This file is kept for history only. It is stale on at least two counts: it says the
> SMTP/Cloudflare ingress adapter is "NOT STARTED", but another agent began it during the
> session (`HostIngressSink.cs`, `IngressComposition.cs`, `HostTransportOptions.cs`,
> `HostIngressServices.cs`, `PrincipalSubmissionAuthenticator.cs`, files that are not mine and
> must not be reverted), and it predates the fleet rule requiring a full `StyloMail.slnx` build
> before declaring done.

---

# `host-`, saved context

## Identity + scope

`host-` owns the ASP.NET Core HTTP host, CLI and operator surface for StyloMail.
**Files I own:** `src/StyloMail.Host/**`, `tests/StyloMail.Host.Tests/**`.
**Never edit:** `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue}/**`,
`.styloagent/spec.md`, `email-proxy-spec.md`, and never `jevkey.pvt`.

## Current state, 2026-09-22

Repo `stylomail`, branch `main`. **Nothing committed** (mission forbids `git add`/`git commit`).
94 tests, all green, no live Jev provider and no network in the suite.

**NEXT AGENT, START HERE.** Two things are done and two are not:

**DONE (verified):** assessor wired (`HostServices.BuildAssessor`, env credentials, fail-fast at
startup in both `Program.cs` and `CliApplication`); `MailAssessment.SubmissionAdmission` adopted
for the 202/200 status; `Idempotency-Key` now REQUIRED on `POST /v1/submissions` (missing → 400
`idempotency_key_required`); the fake models the admission so the field is actually exercised.

**NOT STARTED, this is the remaining work:** the SMTP/Cloudflare ingress wiring. overview-
extended my brief for it on 2026-09-22. Deliverables:
1. Implement `ISmtpIngressSink`, thin translation of inbound bytes + envelope into
   `MailAnalysisInput`, then call the assessor. **Do NOT call `QueueStore.AcceptAsync`**: the
   assessor already does acceptance when `AssessmentOnly = false`. `transport-`'s original sketch
   said "run the pipeline, then AcceptAsync" and that would reintroduce the double-accept that
   cost us duplicate mail, challenge it.
2. Implement `ISubmissionAuthenticator` as a thin adapter over `PrincipalDirectory` (it already
   carries `PrincipalId`, `TenantId`, approved sender identities).
3. Construct `SmtpSubmissionListener(options, sink, authenticator)` and
   `CloudflareEmailRoutingConnector(options, sink)` in `HostServices`/`Program.cs`.
4. Honour the durability rule: acknowledge ONLY after a durable queue row exists; `Defer`
   otherwise. Never build `IngressDecision.Accepted` without a queue id.
Full constructor shapes are in `transport-`'s message "exact-constructor-shapes-the-four-...".
Stopped here deliberately on overview-'s instruction ("a degraded handover is worse than an
early one"), not because it is blocked.

Build/run:
```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
dotnet run --project src/StyloMail.Host -- serve | assess <f.eml> | replay <dir> | quarantine … | profiles …
```

Verified by hand, not only by tests: `serve` returns `/health/live` 200, `/health/ready` 200,
`/metrics` 200; unauthenticated `POST /v1/assessments` → 401; authenticated with no assessor
configured → 503 `assessor_unavailable`; body naming another tenant → 403 `tenant_mismatch`.
CLI `assess` prints 22 deterministic signals and explicitly skips the provider; `--semantic` with
no assessor refuses (exit 3); `replay` is byte-identical across runs.

## Routes and the privilege each requires

| Route | Policy |
| --- | --- |
| `POST /v1/assessments` | `Assess` |
| `POST /v1/submissions` | `Send` |
| `GET /v1/submissions/{id}` | `Send` |
| `GET /v1/decisions/{id}` | `Review` |
| `POST /v1/feedback` | `Feedback` |
| `POST /v1/quarantine/{id}/release` | `Review` |
| `POST /v1/controls/senders/{id}/pause` | `Administer` |
| `POST /v1/controls/senders/{id}/resume` | `Administer` |
| `POST /v1/session` | anonymous; **only mapped when browser channel enabled** |
| `/health/live`, `/health/ready`, `/metrics` | anonymous, and must carry no message content |

## Open items for `host-`

1. **`IMailAssessor` now EXISTS**, `src/StyloMail.Assessment`, owned by `assess-`
   (`AssessmentPipeline.Create(mimeAnalyzer, classifier, sqliteConnectionFactory, spoolStore,
   mailAssessorOptions, queueOptions)`). The Host still registers `UnavailableMailAssessor` and
   returns 503 as the fallback. **overview- ruled 2026-09-22: registering the pipeline is HOST'S
   job, building it is assess-'s. "Do it. Do not ask again."** Keep `UnavailableMailAssessor` as
   the fallback, it is correct behaviour for a deployment with no Assessment config, not a
   placeholder to delete.

   **DONE 2026-09-22.** Credential model decided by overview-: both secrets from ENVIRONMENT
   variables, `TYPESAFE_API_KEY` (use `JevOptions.ApiKeyEnvironmentVariable`, never the literal)
   and `STYLOMAIL_PROFILE_KEY` (`HostCredentials.ProfileKeyEnvironmentVariable`). `jevkey.pvt` is
   explicitly NOT a source. `HostServices.BuildAssessor` resolves them; unconfigured → sentinel;
   half-configured or key < 32 bytes → throws at startup.
   **`Program.cs` and `CliApplication` force `GetRequiredService<IMailAssessor>()` at startup**,    without it the lazy singleton means a half-configured deployment boots, reports healthy, and
   only fails on first mail. Verified: half-configured refuses to start (names the missing var),
   fully configured starts and serves.
   Historical note on what it needed:
   `AssessmentPipeline.Create(mimeAnalyzer, classifier, connections, spool, MailAssessorOptions, queueOptions)`
   requires an `ISemanticMailClassifier` (Jev + API key) and `MailAssessorOptions` with a
   **`required ProfileKeyHasher`**. Both are deployment secrets with no configured source in this
   repo, and `StyloMail.Host` does not yet reference `StyloMail.Assessment`. Decide the config keys
   and where the secrets come from (env / secret store, never a literal, never `jevkey.pvt`)
   before wiring; never invent a secret-store key name.

5. **RESOLVED 2026-09-22, the host no longer accepts. Do not reintroduce an `AcceptAsync`
   call on the submission path.** The assessor is the only component that accepts (spec §4 step 7).
   `SubmissionsEndpoints` spools the payload, passes the client key via
   `AssessmentContext.ClientIdempotencyKey`, and reads `MailAssessment.SubmissionId`. Mapping is
   exhaustive with a refusal default: id → 202; `Reject` → 422; `Defer` → 503; anything else with
   no id → 503, never a 202.
   It would previously have been wrong: both the host and the assessor called `AcceptAsync`,
   under two different idempotency keys, so a submission became two queue entries.

6. **RESOLVED, the test fake was the reason the seam stayed invisible.** `RecordingAssessor` now
   reads the payload back through the durable reference and accepts through the real intake, wired
   through the container rather than `new`.
2. **Two decision ledgers exist.** The Host has `host_decision_ledger` (JSON document, tenancy in
   the PK). Persistence declares its own normalised `decision_ledger` and `feedback`, `recipient_disposition`.
   They collided in the shared database file and broke startup until I renamed mine with a `host_`
   prefix. **Which is canonical is `overview-`'s call**, I did not adopt Persistence's schema
   because its column semantics are its owner's intent, not mine to guess.
3. **RESOLVED 2026-09-22, `/resume` added** (overview- directed). Same `Administer` privilege;
   audited; idempotent; preserves the pause record (`paused_at`/`reason` are not erased by a
   resume, so "why was this account stopped for six hours?" stays answerable). Schema gained
   `resumed_at`/`resumed_by`/`resume_reason` plus a guarded `ALTER TABLE` migration in
   `HostDatabase.ApplyColumnMigrations`, `CREATE TABLE IF NOT EXISTS` is a no-op on an existing
   table, so an earlier database would have failed at the first write naming a new column.
4. **RESOLVED, `quarantine list` now uses `QueueStore.ListAsync`** (`Filter = Quarantined`).
   Verified by grep that no direct `queue_item`/`queue_recipient` access remains in the Host. The
   old direct read would have broken silently the first time the queue's schema moved.

7. **Transport wiring is offered but NOT in my mission.** `transport-` has
   `ISmtpIngressSink` / `ISubmissionAuthenticator` for me to implement and two ingresses
   (`SmtpSubmissionListener`, `CloudflareEmailRoutingConnector`) to feed it. Asked `overview-` to
   arbitrate ownership; told `transport-` not to edit my project meanwhile. **Note their message
   says a sink should "run the pipeline, then `QueueStore.AcceptAsync`", that contradicts
   overview-'s ruling that only the assessor accepts. Challenge it before implementing.**

## Correction I had to make (audit record)

I told `queue-` that `RecipientAdmission.ReEvaluateBy` being required-when-Held meant a null
deadline "will throw", and that their validation was "a hard dependency of my submit path".
**Both false.** It is `DateTimeOffset?`; null resolves to `QueueOptions.DefaultHoldWindow`. I
reasoned from an XML comment that was itself wrong, while their actual code was available to read.
`queue-` has fixed the comment and pinned the real behaviour with a test.

**Lesson worth keeping:** a doc comment is a claim about code, not evidence about it. When a
constraint is load-bearing for my design, read the code, the same discipline I applied to
`RequireDurable` (where reading `PayloadReferences` is what showed `spool://pending` silently
passed the check).

## Infra gotchas learned the hard way

- **`QueueSchema` moved under me mid-build** (v1 → v3, `EnsureCreated` gained a `TimeProvider`).
  I no longer call it: `QueueStore.InitializeAsync` owns its schema. Do not re-add a direct call.
- **`WebApplicationFactory` passes its own argv to the entry point.** The CLI parser must treat a
  leading `--option` as host configuration, not a command name, or the test host never builds.
- **Antiforgery tokens are bound to the authenticated claims identity.** Minting one while the
  request still looks anonymous (`SignInAsync` does not change `context.User`) makes every later
  legitimate request fail validation. Set `context.User` before `GetAndTokenStore`.
- **Minimal hosting reads configuration before `ConfigureWebHost` layers the test config in.** Never
  decide scheme registration from `configuration.GetSection(...)` at startup, bind options and
  resolve them per request. This bit me once already.
- Config keys: `StyloMail:Storage:SpoolRoot`, `StyloMail:Storage:DatabasePath`,
  `StyloMail:Auth:Principals:<n>:{PrincipalId,Key,TenantId,Privileges:<n>}`,
  `StyloMail:Auth:EnableBrowserCookieChannel`, `StyloMail:Auth:CookieLifetime`.
- **Secrets:** API keys come from configuration (env vars / secret store in a real deployment).
  Nothing sensitive is stored in this repo. `jevkey.pvt` is the Jev provider key and is not mine.

## Hard rules I hold

- Tenant authority comes from the authenticated principal, never a request body. A conflicting body
  `tenantId` is 403, not honoured.
- `202` on submission requires a durable queue id; storage failure is 503, never 202.
- Assessment has no delivery implication and creates no queue state.
- Cross-tenant reads of decisions/submissions return **404**, identical to a nonexistent id, no
  existence oracle.
- A sender cannot release its own quarantine; shadow mode requires `Administer`.
- No request bodies in logs; health and metrics carry no message content.
- CLI assessment never transmits content externally unless `--semantic` is passed explicitly.

## Mutation results (2026-09-22)

Mutation: delete the host's replay fast-path (`FindAsync` → `existing = null`).

| Test | Result |
| --- | --- |
| `A_retry_does_not_spend_a_second_assessment` | **RED**, has teeth |
| `A_retry_with_the_same_key_and_payload_returns_the_same_queue_id` | **GREEN, did not catch it** |

The second one was *already* known-suspect and I tried to fix it by asserting the response
`status` field. **The mutation proved that fix cosmetic**: the queue's own dedup also reports
`"Duplicate"`, so the field does not discriminate. That is the advisory's variant-1 pattern, two
mechanisms, one observable outcome, recurring inside my attempt to fix it.

Resolution: the test comment now states plainly what it does and does not establish, rather than
asserting the same outcome more loudly. The mechanism claim rests on the assessor-invocation count.

Restore verified by grep for residue (advisory Trap 2, never assume the restore).

**Seam fix re-run after the rewrite:** the same mutation now turns BOTH tests red. The replay test
asserts the assessor was invoked once, something only the host path can produce.

**Resume mutations:**
- `ResumeAsync` made a no-op → 5 of 7 resume tests red. Initially only 3 caught it;
  `Resuming_does_not_erase_the_record_that_the_pause_happened` and
  `A_resume_cannot_reach_another_tenants_sender` both PASSED against a no-op, because both are
  "nothing happened" claims. Repaired to be two-sided (lifted *and* history retained; untouched
  for the other tenant *and* applied in the acting tenant's own namespace). Now both red.
- Tenant scope dropped from the resume write → cross-tenant test red.

**The test fake was the root cause of the seam staying invisible.** `RecordingAssessor` now reads
the payload back through the durable reference and accepts through the real intake, wired via the
container. It deliberately has NO fallback idempotency key, matching `MailAssessor` exactly, inventing one made the fake kinder than production, so the no-key case looked replay-protected.
