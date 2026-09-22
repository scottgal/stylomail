# `ingress-` — checkpoint

Written on request, standing by. My longer living checkpoint is
`.styloagent/channel/saved-context/ingress--context.md`; this file is the cold-start handover.

## Identity + scope

`ingress-` owns the **Host ingress wiring** and, since then, the **management surface**:
`src/StyloMail.Host/**` and `tests/StyloMail.Host.Tests/**`.
**Never edit:** anything under `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue,Assessment,Transport,AccessProxy}/**`,
`.styloagent/spec.md`, `email-proxy-spec.md`, `jevkey.pvt`.

Took over from `host-` (its checkpoint is still worth reading for the Host's own history).

## State

- Branch `main`. **My lane is committed at `1f9cf98`** — "Land the listing, credential and management
  routes the console reads".
- **213 Host tests, green.** `dotnet build StyloMail.slnx`: **0 errors / 0 warnings** (at my last
  measurement).
- Two live verification scripts, both against the real binary:
  - `/tmp/stylomail-probe/docs_probe.py` — **49/49** (startup, CLI, config, listings, credential
    readiness; backs `docs/running.md`)
  - `/tmp/stylomail-probe/probe.py` — **31/31** (SMTP ingress and Cloudflare route end to end)
  - Counts drift. Re-run rather than trusting these numbers.
- **Nothing in flight.** No git commits from me — my mission forbids `git add`/`git commit`;
  `overview-` commits the lanes.

## MEASUREMENT DISCIPLINE — read this before believing any number

Six agents edit this tree at once and `queue-`'s mutation harness rewrites `src/` **in place**. A Host
failure is far more likely to be someone else's live mutation than mine — `StyloMail.Host` references
Queue, Assessment, Adaptive, Mime, Policy, Persistence and Transport.

```
ls .styloagent/tools/.mutation-sweep.lock   # sweep running now
find src -name '*.bak'                      # sweep SIGKILLed, mutation still applied
```

Both clean = the measurement is real. This cost `access-` three wrong attributions in one afternoon,
both directions.

**And a near-miss in my own harness, worth not repeating:** my gated stress loop once counted **twelve
runs that never executed** — a stale shell cwd meant `dotnet test` could not find its project, printed
no `Failed!` line, and a loop counting only failures recorded twelve passes. **Every loop must require
`Passed!`/`Failed!` in the output and count anything else as *not executed*.** A loop that counts only
failures is blind to its own silence.

**When a probe disagrees with a passing unit test, suspect the probe.** Mine cost three rounds by
pointing the Jev endpoint at a Python `http.server` stub that defaulted to HTTP/1.0 and answered
without reading the request body — the client saw a *connection fault*, which the adapter correctly
degrades to `Unavailable`. The host was right the whole time.

## What I built

**The four original mission items**
1. `HostIngressSink` — spools bytes through the shared `SpoolStore`, calls `IMailAssessor.AssessAsync`
   with `AssessmentOnly = false` and **no client key**. **No queue and no intake in its constructor**,
   so the double-accept is structurally impossible. `Allow` with no `SubmissionId` is a Defer, never a
   250. Reason *codes* only to the client, never prose.
2. Both listeners constructed (`SmtpSubmissionListener` hosted, gated on `SmtpIngress:Enabled`;
   `CloudflareEmailRoutingConnector` singleton) + `PrincipalSubmissionAuthenticator` over
   `PrincipalDirectory` + `ApprovedSenderIdentities` on `HostPrincipalOptions`.
3. `QueueDeliveryWorker` hosted as a `BackgroundService` forwarding the shutdown token.
4. Two composition assertions in `IngressComposition`, at construction, naming both values:
   `transportMax <= queueMax`, and **one `SpoolStore` by reference identity**.

**Since then** (all committed in `1f9cf98`):
- `POST /v1/ingress/cloudflare` — bearer auth from `STYLOMAIL_CF_INGRESS_SECRET`, mapped only when
  enabled, Kestrel's body limit raised to the connector's max.
- `GET /v1/messages`, `GET /v1/senders`, `GET /v1/decisions` — `Review`, tenant-scoped **from the
  principal with no tenant parameter** so a cross-tenant read is *absent* rather than forbidden.
- `GET /v1/submissions/{id}` now accepts **`Send` or `Review`** via `HostPolicies.SendOrReview`.
- `?messageId=` on the decisions listing + `internalMessageId` on message rows — the link from a
  listed message to its decision.
- **A rejected provider credential now reports not-ready** (`provider_credential` in
  `/health/ready`); liveness stays 200.
- `JevOptions.Endpoint`/`Model` bind, with a startup WARNING when the endpoint is not the default.
- **Operator metadata**: `GET/PUT /v1/senders/{id}/settings`, `GET/POST /v1/companies`,
  `PUT /v1/companies/{id}`; `label`/`companyId` on the sender listing.
- `docs/running.md` — the executable's operating documentation, every claim checked by running.

## Hard rules I hold

- **Only the assessor accepts.** Never call `QueueStore.AcceptAsync` from the ingress path.
- **A 250/2xx requires a durable queue id.**
- Secrets come only from the environment (`TYPESAFE_API_KEY`, `STYLOMAIL_PROFILE_KEY`,
  `STYLOMAIL_CF_INGRESS_SECRET`) — never a config key, never `jevkey.pvt`.
- **Never serialise `HostPrincipalOptions`** — it holds the API key. Name each field.
- Refuse a filter value the query cannot honour, **by name**, rather than post-filtering a page.
- Tenant authority comes from the authenticated principal, never a request body.

## Handed off (no longer mine — do not start these)

- **`keys-`** owns the minted-credential path: principal store, `key` CLI, `PrincipalDirectory`
  resolution. `overview-` ruled five guards: **precedence is total and never merged** (store wins
  wholesale); `key list` reports which source resolved each principal; `key revoke` **refuses on an
  environment principal and names the config that owns it**; the digest is a **slow KDF**, per-key
  salt, constant-time compare; **revocation must defeat any resolution cache**. `key create` prints
  once to stdout and nowhere else. `keys-` is live in this tree now — its edits to
  `PrincipalDirectory.cs` and `HostAuthentication.cs` are visible.
- **`hub-`** owns the SignalR traffic hub, in an **isolated worktree**. My emission map went into its
  mission verbatim: assessment completed at `SqliteDecisionLedger.RecordAsync`; message state at
  `QueueDeliveryHostedService` and `QuarantineEndpoints`; pause/resume at `ControlsEndpoints`;
  readiness **on the transition**, not every poll. `ITrafficEvents` port with a **no-op default** (the
  flag-off path), hub **off by default**, and the hard rule: **no pipeline code may depend on it — an
  emission must never be able to fail an assessment or a delivery.**

## Open, reported to their owners

- **The spool peak, measured.** Every message is spooled *before* assessment, so a deferred or refused
  message leaves bytes on disk with **no queue row referencing them** — and
  `MaxLivePayloadBytesPerTenant` is computed from `queue_item.payload_bytes`, so those bytes are
  bounded by *nothing*. Measured live: **32.0 MB across 2 payloads, 0 queue rows accounting for it**,
  reclaimed only by the hourly orphan sweep. **Delete-after-accept is deliberately not started.**
- **`POST /v1/assessments` and `/v1/submissions` answer 500 with an empty body** on a credential
  rejection — not a safety defect (nothing is accepted) but a poor answer for a condition the host
  already names in readiness. The Jev adapter's throw is `overview-`'s.
- **Stale docs, not mine to edit:** `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` still say
  the null sender "may be, as in a bounce", which the ruling made false.

## Lessons worth more than the code

- **Inverting a characterisation test beats deleting it.** A test pinning components *agreeing* is a
  guard; remarks saying they once disagreed are history. I deleted one and was told twice to invert it.
- **A fixture chosen deliberately is not a defence.** `queue-`'s paging test gave every item the same
  timestamp *on purpose* to exercise the tiebreaker — and equal timestamps are exactly the condition
  under which the cursor bug was invisible. My own ingress submissions all used `HopCount = 0`, which
  would have hidden a null-versus-zero mistake in the sink just as thoroughly.
- **The discriminator for real-vs-phantom intermittency** (`queue-`'s formulation, better than mine):
  the signals that were **real did not change with the input**; the ones that were not **varied with an
  input**. Same test, same property, every time = real.
- **A name is a claim.** My `AcceptanceCounter.Accepted` counted *calls*, including refused ones, and
  a test of mine failed because the count was right and the name was a lie. Renamed `AcceptAttempts`.
- **A red test in a shared suite must be self-describing** — catch, and `Assert.Fail` naming the
  defect, the owning lane and the word EXPECTED. Delete the scaffolding once it goes green.
- **"Verified" must name its scope.** `docs/running.md` claimed every claim was checked by running, and
  that was true — while the `127.0.0.1:5000` example went unverified and is **macOS AirPlay Receiver**,
  answering 403. An example is a claim.
