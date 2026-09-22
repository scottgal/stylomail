# `ingress-` — saved context

## Identity + scope

`ingress-` owns **Host ingress wiring**: the `ISmtpIngressSink` adapter, listener construction, the
delivery-worker hosting, and the remaining composition assertions.
**Files I own:** `src/StyloMail.Host/**`, `tests/StyloMail.Host.Tests/**`.
**Never edit:** anything under `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue,Assessment,Transport,AccessProxy}/**`,
`.styloagent/spec.md`, `email-proxy-spec.md`, and never `jevkey.pvt`.

Took over from `host-` on 2026-09-22; its checkpoint is `.styloagent/channel/saved-context/host--context.md`
and is still worth reading for the host's own history.

## State — 2026-09-22

Repo `stylomail`, branch `main`. **Nothing committed** (the repo has no commits at all; mission
forbids `git add`/`git commit`). **150 Host tests, green — 0 failures across 20 consecutive runs on a
tree verified clean before every run.** `dotnet build StyloMail.slnx`: **0 errors, 0 warnings.**
Live probe: **31/31 checks** (`/tmp/stylomail-probe/probe.py`).

**Measure with the tree signals, not with your eyes.** Six agents edit this tree at once and `queue-`'s
mutation harness rewrites `src/` **in place**. A Host failure is far more likely to be someone else's
live mutation than yours — `StyloMail.Host` references `Queue`, `Assessment`, `Adaptive`, `Mime`,
`Policy`, `Persistence` and now `Transport`, so almost any sweep can move a Host victim. Before
believing any failure, or any *green* run:

```
ls .styloagent/tools/.mutation-sweep.lock   # sweep running now
find src -name '*.bak'                      # sweep SIGKILLed, mutation still applied
```

Both clean = the measurement is real. This cost `access-` three wrong attributions in one afternoon,
in both directions.

Build/run:
```
export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"
dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj
dotnet run --project src/StyloMail.Host -- serve | assess <f.eml> | replay <dir> | profiles …
```

## What I built (all four mission items)

1. **`HostIngressSink`** (`src/StyloMail.Host/Hosting/HostIngressSink.cs`) — spools the raw bytes
   through the shared `SpoolStore` under `ingress-<internalMessageId>`, translates into a
   `MailAnalysisInput`, calls `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and
   `ClientIdempotencyKey = null`, then reads `SubmissionId`. **It has no queue and no intake in its
   constructor**, so the double-accept is structurally impossible rather than merely avoided.
2. **Both listeners constructed** in `HostServices.AddTransport` — `SmtpSubmissionListener` (hosted,
   gated on `SmtpIngress:Enabled`) and `CloudflareEmailRoutingConnector` (container singleton, built
   over the same sink). Plus `PrincipalSubmissionAuthenticator` over `PrincipalDirectory`, without
   which the SMTP listener's AUTH path is dead.
3. **`QueueDeliveryWorker` hosted** as `QueueDeliveryHostedService : BackgroundService`, forwarding
   the shutdown token to `RunAsync` so its bounded drain works. The `SmtpDeliveryPort` is built
   inside it rather than registered, because "is there a port?" and "does the worker run?" are the
   same question.
4. **Two composition assertions** in `IngressComposition`, called from the composition factories at
   construction, failing with both values named — and both verified against a real process.

## The two assertions, and the evidence they have teeth

- `RequireIngressFitsQueue(ingressName, ingressMax, queueOptions)` — asserted for both ingresses
  regardless of whether they are enabled.
- `RequireSharedSpool(ingressSpool, pipelineSpool, description)` — **reference identity, not path
  equality**, so a second instance over the same directory is refused.
- `RequireSpoolRoot(spool, configuredRoot)` — the second half: one instance, rooted where the
  deployment says.

Verified live, not only in tests: a host configured with `SmtpIngress:MaxMessageBytes = 999999999`
**refuses to start** (`exit 134`) naming both values.

## Live probe — 31/31 checks, `/tmp/stylomail-probe/probe.py`

Real `dotnet run -- serve` process, real socket, no provider secrets. Proved: both ports bind;
`/health/live`, `/health/ready`, `/metrics` all 200; SMTP greeting/EHLO/MAIL/RCPT/DATA/QUIT; the
open-relay refusal (550 for an unserved domain); a served-domain message **not** acknowledged without
a durable row (451, assessor unconfigured); the payload spooled through the shared spool; **exactly
one `Received:` line prepended with everything after it byte-for-byte the sent message**; the hop
marker names no recipient; `queue_item = 0`. The startup posture line and its WARNING both appear.

**The Cloudflare phase (second host, real Kestrel).** Wrong secret → 401, no secret → 401,
authenticated Worker to an unserved domain → 403, served domain → not refused, route absent when
disabled → 404, over-maximum body refused, **no oversized payload reached the spool**, and the
ingress copy byte-identical to the Worker's bytes after exactly one `Received` line. Plus the check
the suite cannot make: **a 32 MB body returned 503, not 413** — the endpoint's limit raise works.

**What the probe could not prove, and why:** the *acceptance* mapping needs a working assessor, and
this deployment has no provider secrets — `JevOptions.Endpoint` exists but `HostServices.BuildAssessor`
never reads it, so the real classifier can only be pointed at the live TypeSafe endpoint. Acceptance
is therefore proven in the suite instead, against the container-wired fake assessor with a **real
queue row on disk**. Do not read the probe as having exercised a 250 — every live message deferred.

## The transport defect — found, fixed by its owner, workaround now reverted

`SmtpSubmissionListener.StopAsync` then `DisposeAsync` threw `ObjectDisposedException` out of a live
session's cleanup on host shutdown: `StopAsync` nulled `_listener` before draining, so the later
`DisposeAsync` returned early **without joining the drain** and freed `_connectionLimit` while
sessions were still releasing it. Every existing transport test used `await using` and never called
`StopAsync`, so their suite never saw the pair. Neither did mine — it took actually hosting the thing.

- Reported and filed. **`transport-` fixed it**: every stop-caller now gets the same drain task, so a
  second caller waits rather than returning early. They also reset the recorded task in `Start()` —
  a bug they would have introduced *with* the fix and caught by reading their own change.
- **My workaround is reverted.** `SmtpIngressHostedService` is back to the ordinary
  `IHostedService` + `IAsyncDisposable` pair. The history is kept in its remarks as the lifecycle
  lesson: a component that can only be stopped *or* disposed, never both, cannot be hosted.
- **Verification of the revert, with the same measurement that convicted it:** 7-of-15 red with the
  old `StopAsync` and no workaround; **0 failures across 25 clean runs** with the fix and no
  workaround. Live probe is 21/21 with the dispose back in, so the pairing is exercised in a real
  SIGTERM shutdown, not only under `WebApplicationFactory`.
- `SmtpIngressTests.Shutting_down_with_a_client_still_attached_does_not_fail` stays — it parks a
  client mid-session, which is what makes the in-flight session deterministic rather than lucky.

## Contract friction — resolved, and one measured finding

**RESOLVED: the null sender, both directions.** `queue-` scoped the refusal to
`Direction == Outbound`, moved it out of `ValidateSubmission` (where `Require(MailFrom)` was doing two
different jobs — a construction error and a policy refusal), and made it **return**
`QueueAdmission.RefusedNullSender` rather than throw, so an ordinary policy outcome no longer leaves
the assessor as an unhandled exception. Two tests hold it: an **inbound** DSN is accepted end to end
with a real queue row, and an **outbound** null sender is refused by all three components. The latter
is the characterisation test `overview-` and `transport-` told me to *invert* — I had deleted it, which
was the wrong call: a test pinning three components' agreement is a guard, whereas remarks saying they
once disagreed were only history.

**RESOLVED: `HopCount`.** `overview-` added `int? HopCount` to `MailEnvelope`, `assess-` copies it in
`Step7Async`, and the sink populates it. **Always the number, never null** — both ingresses scan before
they call in, so a count exists and reporting null would tell the queue its backstop had not run when
it had. Not verified that the backstop *fires* (needs a message with `MaxHops` prior Received headers
reaching acceptance) — say so rather than imply it.

**OPEN, and now measured: the spool peak.** Every message is spooled *before* it is assessed, so a
message that is then deferred or refused leaves its bytes on disk with **no queue row referencing
them** — and `MaxLivePayloadBytesPerTenant` is computed from `queue_item.payload_bytes`, so those bytes
are bounded by *nothing*. Measured on a live host: **32.0 MB on disk across 2 payloads with 0 queue
rows accounting for it**, reclaimed only by the orphan sweep after `OrphanSweepMinimumAge` (1 hour). So
an inbound flood of deferring messages — trivially produced when the assessor is unavailable — has a
`rate × 1h × max-message-size` ceiling that no configured bound accounts for. **Any deletion is still
not started**; this measurement is what reopens the peak question with `queue-`.

**STALE DOCS, not mine to edit:** `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` both still say
the null sender "may be, as in a bounce", which the ruling made false on both.

**The ingress spool copy is not deleted.** Every accepted message is written to disk twice: the
ingress copy the pipeline reads back from, and the queue's own copy under the queue id, which is the
acceptance. The ingress copy is referenced by no metadata and the orphan sweep collects it after
`OrphanSweepMinimumAge` (1 hour). **Delete-after-accept is deliberately not started** — the mission
forbids it while two questions are open with `queue-` (whether the spool roots are shared, and the
measured peak). Pinned by `IngressSinkTests.An_accepted_message_leaves_two_payloads_on_the_spool`, so
the change that removes it has something to fail against.

## The Cloudflare intake — `POST /v1/ingress/cloudflare`

Added at `overview-`'s ruling. `Endpoints/CloudflareIngressEndpoints.cs`, mapped in `Program.cs`
**only when `CloudflareIngress:Enabled`** (a route that exists and always refuses invites a
configuration change to "fix" it; a 404 says the deployment has no such intake).

- **Bearer auth**, secret from `HostCredentials.CloudflareIngressSecretEnvironmentVariable`
  (`STYLOMAIL_CF_INGRESS_SECRET`). There is deliberately **no `SharedSecret` config property** — the
  name is defined once beside the other two and the value is never written down.
- **Enabled without the secret refuses to start**, via the pure
  `HostCredentials.RequireCloudflareIngressSecretIfEnabled(secret, enabled)` — pure so it is testable
  without mutating process environment, which races against the whole suite.
- Raw RFC 5322 body, envelope in `X-StyloMail-Envelope-To` / `-From` headers, tiny JSON reply.
- **Kestrel's body limit is raised to the connector's own maximum for this endpoint** via an endpoint
  filter setting `IHttpMaxRequestBodySizeFeature`. Its 30 MB default is below the connector's 64 MB, so
  without this a message between the two is refused by the server with a 413 that names no component.
- Two gates, both required: the Worker's secret, and a recipient in a served domain. The second is what
  still holds if the first leaks.

**`WebApplicationFactory` runs `TestServer`, which does NOT enforce a request body limit** — so the
suite physically cannot verify that filter. The live probe can, and does: a **32 MB body returns 503,
not 413**. Do not let a future reader "simplify" that filter on the strength of a green suite.

## Assertions added after the wiring landed

- **`The_hop_count_an_ingress_observed_reaches_the_envelope_the_pipeline_sees`** — theory over
  0/7/19. `transport-` flagged that `HopCount = submission.HopCount` was the last link in a chain
  already inert once, with no test reading a count back. **Mutation-verified: deleting that line turns
  all three cases red.** Zero is a case, not the default — the sink *did* look, so null would be a
  false claim about our own behaviour.
- **`AdmissionRefusalMappingTests`** — `overview-` expected a `QueueAdmission` switch in the Host
  needing `RefusedNullSender = 7` added. **There is no such switch**: the Host maps
  `MailAssessment.SubmissionId` + `MailAction` and never reads the queue's admissions, so a new enum
  member cannot change an outcome. Since that is a claim about code shape, the tests drive a real
  `RefusedNullSender` from the real `QueueStore` out through both Host edges and assert neither can
  answer 2xx/250.

## Names that were lies

Two caught in one afternoon, both mine, both the same class as the stale comments and the unfed
`MaxHops` — **a name read as a fact**:

- `AcceptanceCounter.Accepted` counted *calls* to the intake, including refused ones. A new test
  asserted 0 on a run where the queue was called once and refused; the count was right and the name
  was wrong. Renamed `AcceptAttempts`, documented as deliberately counting calls — the double-accept
  defect is two *calls*, so that is the only place the second one is visible.
- `IngressComposition.RequireSharedSpool` compares by reference, which is correct, but a reader could
  take "shared" to mean same-path; the doc says why it must be identity.

## Follow-up work landed after the first report

- **CS0168 cleared** (`SubmissionsEndpoints.cs`). `overview-` asked whether the discarded exception
  was an oversight or deliberate; it is deliberate, and now says so. `SpoolUnavailableException`
  comes from the queue's spool and its messages name the **spool directory, the errno and the queue
  item id** — returning that to `POST /v1/submissions` would disclose internal paths and identifiers.
  The sibling handler that *does* surface a message uses the host's own `StorageUnavailableException`,
  which carries a fixed sentence. Now an unbound `catch` with the reasoning in a comment, so the
  asymmetry reads as intended rather than as sloppiness. `overview-` was also asked whether to turn
  on `TreatWarningsAsErrors` repo-wide — clean today, so cheap now.
- **`ClosedPort()` TOCTOU removed** (`DeliveryWorkerHostingTests`). A probe-then-release port helper:
  between `Stop()` and the host's later bind, the OS may hand the port to anyone, including a parallel
  test class. Flagged by `access-` and `assess-`. Fixed by **removing the need for it** rather than
  narrowing the window: the upstream target is now `192.0.2.1` (TEST-NET-1, RFC 5737), never assigned
  and never routable, so no port is allocated at all. A race that cannot fire today is one that fires
  the day the test acquires a reason to dial.

## Deliberately not done

1. ~~No HTTP intake route for the Cloudflare connector~~ — **done**, at `overview-`'s ruling. See the
   Cloudflare section above.
2. **`JevOptions.Endpoint` is still not host-configurable.** Left alone because a config key that can
   redirect message content is an operator privacy decision, not a wiring gap. It is what blocks a
   fully live end-to-end acceptance probe against a local stub. Suggested to `overview-`.
3. **No `SmtpSubmissionListener`/`SpoolStore`/`QueueStore` changes** — none are mine.

## Infra gotchas learned the hard way

- **The host's composition root runs before `WebApplicationFactory` layers test config in.** Anything
  that decides from `IConfiguration` at `AddStyloMailHost` time reads the wrong values. This is why
  `SmtpIngress:Enabled` is honoured in `SmtpIngressHostedService.StartAsync` and not at registration,
  and why the transport options are resolved through `IOptions<>` inside factories.
- **The Host now references `StyloMail.Transport`** (added to `StyloMail.Host.csproj`). Transport does
  not reference Host; no cycle.
- **`X509CertificateLoader.LoadPkcs12FromFile`**, not the obsolete `X509Certificate2` constructor —
  the old form is a build error under this repo's analyzers. `DefaultKeySet`, not `EphemeralKeySet`
  (the latter cannot be used by `SslStream` for server auth on every platform).
- **A test that passes against a fake can still be green over a broken seam.** Every ingress test runs
  against `RecordingAssessor`, which never runs the pipeline's step one — so `IngressPipelineSeamTests`
  exists to run the *real* `AssessmentValidation` over the exact input the sink builds. Mutation-verified:
  setting `ParserLimitExceeded = true` in the placeholder turns it red.
- **`TestServer` does not enforce a request body limit.** Anything about request sizes, Kestrel limits
  or server-level refusal is invisible to the suite and has to be probed against a real process.
- **A red test in a shared suite is indistinguishable from a lane's breakage.** When asked to write a
  test that is expected to fail, make the failure self-describing — catch, and `Assert.Fail` with the
  defect, the owning lane and the word EXPECTED — because three agents lost part of an afternoon this
  way. Delete the scaffolding once it goes green.
- **Inverting a characterisation test beats deleting it.** A test that pins components *agreeing* is a
  guard; remarks recording that they once disagreed are history. I deleted one and was told twice to
  invert it; they were right.
- **`dotnet test --no-build` after editing a source file tests the previous mutation.** Cost me a
  confusing 5-of-10 red run that was really the mutation still on disk.
- Judge this work with `dotnet test tests/StyloMail.Host.Tests/…`, never the solution build —
  several agents edit `StyloMail.slnx` concurrently.

## Config surface added (`StyloMail:Transport`)

`SmtpIngress:{Enabled,BindAddress,Port,ServerName,CertificatePath,CertificatePassword,RequireEncryption,
AllowUnauthenticatedInbound,RecipientDomains,InboundTenantId,LocalHostIdentities,MaxMessageBytes}` ·
`CloudflareIngress:{Enabled,RecipientDomains,InboundTenantId,ConnectorId,LocalHostIdentities,
ByHost,MaxMessageBytes}` · `Upstream:{Host,Port,Tls,ImplicitTls,Username,Password,HeloName}`.
Also `StyloMail:Auth:Principals:<n>:ApprovedSenderIdentities:<n>` — **empty authorises nothing** beyond
the null sender. The Cloudflare secret has **no config key by design**; it is
`STYLOMAIL_CF_INGRESS_SECRET` only — see `HostCredentials`.

Everything defaults to off. `RequireEncryption` defaults true, so enabling the listener with no
certificate refuses to start — deliberate, and the transport says why with its own message.

## Hard rules I hold

- **Only the assessor accepts.** Never call `QueueStore.AcceptAsync` from the ingress path.
- **A 250 requires a durable queue id.** `Allow` with no `SubmissionId` is a deferral, not an ack.
- Secrets come only from `TYPESAFE_API_KEY` and `STYLOMAIL_PROFILE_KEY`; `jevkey.pvt` is not a source.
- No internal prose in an SMTP reply — reason *codes* only.
- Do not start delete-after-accept until `queue-` answers the two open questions.
