# `ingress-`, saved context

## Identity + scope

`ingress-` owns **Host ingress wiring**: the `ISmtpIngressSink` adapter, listener construction, the
delivery-worker hosting, and the remaining composition assertions.
**Files I own:** `src/StyloMail.Host/**`, `tests/StyloMail.Host.Tests/**`.
**Never edit:** anything under `src/StyloMail.{Core,Persistence,Jev,Mime,Adaptive,Policy,Queue,Assessment,Transport,AccessProxy}/**`,
`.styloagent/spec.md`, `email-proxy-spec.md`, and never `jevkey.pvt`.

Took over from `host-` on 2026-09-22; its checkpoint is `.styloagent/channel/saved-context/host--context.md`
and is still worth reading for the host's own history.

## State, 2026-09-22

Repo `stylomail`, branch `main`. **Nothing committed** (the repo has no commits at all; mission
forbids `git add`/`git commit`). **213 Host tests, green.** `dotnet build StyloMail.slnx`: **0 errors, 0 warnings.**

Two verification scripts, both run against the real binary:
`/tmp/stylomail-probe/docs_probe.py` — **49/49** (startup, CLI, config, listings, credential
readiness; backs `docs/running.md`) and `/tmp/stylomail-probe/probe.py` — **31/31** (the SMTP ingress
and the Cloudflare route end to end). Counts drift; re-run rather than trusting these numbers.

*(This line said 150 tests and 31/31 until it was corrected — a checkpoint that outlives its truth is
the same defect as a stale doc comment, and worse here because a fresh reader cold-starts from it.)*

**Measure with the tree signals, not with your eyes.** Six agents edit this tree at once and `queue-`'s
mutation harness rewrites `src/` **in place**. A Host failure is far more likely to be someone else's
live mutation than yours, `StyloMail.Host` references `Queue`, `Assessment`, `Adaptive`, `Mime`,
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

1. **`HostIngressSink`** (`src/StyloMail.Host/Hosting/HostIngressSink.cs`), spools the raw bytes
   through the shared `SpoolStore` under `ingress-<internalMessageId>`, translates into a
   `MailAnalysisInput`, calls `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and
   `ClientIdempotencyKey = null`, then reads `SubmissionId`. **It has no queue and no intake in its
   constructor**, so the double-accept is structurally impossible rather than merely avoided.
2. **Both listeners constructed** in `HostServices.AddTransport`, `SmtpSubmissionListener` (hosted,
   gated on `SmtpIngress:Enabled`) and `CloudflareEmailRoutingConnector` (container singleton, built
   over the same sink). Plus `PrincipalSubmissionAuthenticator` over `PrincipalDirectory`, without
   which the SMTP listener's AUTH path is dead.
3. **`QueueDeliveryWorker` hosted** as `QueueDeliveryHostedService : BackgroundService`, forwarding
   the shutdown token to `RunAsync` so its bounded drain works. The `SmtpDeliveryPort` is built
   inside it rather than registered, because "is there a port?" and "does the worker run?" are the
   same question.
4. **Two composition assertions** in `IngressComposition`, called from the composition factories at
   construction, failing with both values named, and both verified against a real process.

## The two assertions, and the evidence they have teeth

- `RequireIngressFitsQueue(ingressName, ingressMax, queueOptions)`, asserted for both ingresses
  regardless of whether they are enabled.
- `RequireSharedSpool(ingressSpool, pipelineSpool, description)`, **reference identity, not path
  equality**, so a second instance over the same directory is refused.
- `RequireSpoolRoot(spool, configuredRoot)`, the second half: one instance, rooted where the
  deployment says.

Verified live, not only in tests: a host configured with `SmtpIngress:MaxMessageBytes = 999999999`
**refuses to start** (`exit 134`) naming both values.

## Live probe, 31/31 checks, `/tmp/stylomail-probe/probe.py`

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
the suite cannot make: **a 32 MB body returned 503, not 413**, the endpoint's limit raise works.

**What this probe could not prove, and why:** the *acceptance* mapping needs a working assessor, and
this deployment has no provider secrets. (`JevOptions.Endpoint` IS bindable now — see the credential
section — so the classifier can be pointed at a local stub; what is still missing is a stub that
answers *successfully* with valid Noul answers, which is why acceptance remains proven in the suite
against the container-wired fake assessor with a **real queue row on disk**. Do not read the probe as
having exercised a 250 — every live message deferred or was refused.)

## The transport defect, found, fixed by its owner, workaround now reverted

`SmtpSubmissionListener.StopAsync` then `DisposeAsync` threw `ObjectDisposedException` out of a live
session's cleanup on host shutdown: `StopAsync` nulled `_listener` before draining, so the later
`DisposeAsync` returned early **without joining the drain** and freed `_connectionLimit` while
sessions were still releasing it. Every existing transport test used `await using` and never called
`StopAsync`, so their suite never saw the pair. Neither did mine, it took actually hosting the thing.

- Reported and filed. **`transport-` fixed it**: every stop-caller now gets the same drain task, so a
  second caller waits rather than returning early. They also reset the recorded task in `Start()`,   a bug they would have introduced *with* the fix and caught by reading their own change.
- **My workaround is reverted.** `SmtpIngressHostedService` is back to the ordinary
  `IHostedService` + `IAsyncDisposable` pair. The history is kept in its remarks as the lifecycle
  lesson: a component that can only be stopped *or* disposed, never both, cannot be hosted.
- **Verification of the revert, with the same measurement that convicted it:** 7-of-15 red with the
  old `StopAsync` and no workaround; **0 failures across 25 clean runs** with the fix and no
  workaround. Live probe is 21/21 with the dispose back in, so the pairing is exercised in a real
  SIGTERM shutdown, not only under `WebApplicationFactory`.
- `SmtpIngressTests.Shutting_down_with_a_client_still_attached_does_not_fail` stays, it parks a
  client mid-session, which is what makes the in-flight session deterministic rather than lucky.

## Contract friction, resolved, and one measured finding

**RESOLVED: the null sender, both directions.** `queue-` scoped the refusal to
`Direction == Outbound`, moved it out of `ValidateSubmission` (where `Require(MailFrom)` was doing two
different jobs, a construction error and a policy refusal), and made it **return**
`QueueAdmission.RefusedNullSender` rather than throw, so an ordinary policy outcome no longer leaves
the assessor as an unhandled exception. Two tests hold it: an **inbound** DSN is accepted end to end
with a real queue row, and an **outbound** null sender is refused by all three components. The latter
is the characterisation test `overview-` and `transport-` told me to *invert*, I had deleted it, which
was the wrong call: a test pinning three components' agreement is a guard, whereas remarks saying they
once disagreed were only history.

**RESOLVED: `HopCount`.** `overview-` added `int? HopCount` to `MailEnvelope`, `assess-` copies it in
`Step7Async`, and the sink populates it. **Always the number, never null**, both ingresses scan before
they call in, so a count exists and reporting null would tell the queue its backstop had not run when
it had. Not verified that the backstop *fires* (needs a message with `MaxHops` prior Received headers
reaching acceptance), say so rather than imply it.

**OPEN, and now measured: the spool peak.** Every message is spooled *before* it is assessed, so a
message that is then deferred or refused leaves its bytes on disk with **no queue row referencing
them**, and `MaxLivePayloadBytesPerTenant` is computed from `queue_item.payload_bytes`, so those bytes
are bounded by *nothing*. Measured on a live host: **32.0 MB on disk across 2 payloads with 0 queue
rows accounting for it**, reclaimed only by the orphan sweep after `OrphanSweepMinimumAge` (1 hour). So
an inbound flood of deferring messages, trivially produced when the assessor is unavailable, has a
`rate × 1h × max-message-size` ceiling that no configured bound accounts for. **Any deletion is still
not started**; this measurement is what reopens the peak question with `queue-`.

**STALE DOCS, not mine to edit:** `MailEnvelope.MailFrom` and `QueueSubmission.MailFrom` both still say
the null sender "may be, as in a bounce", which the ruling made false on both.

**The ingress spool copy is not deleted.** Every accepted message is written to disk twice: the
ingress copy the pipeline reads back from, and the queue's own copy under the queue id, which is the
acceptance. The ingress copy is referenced by no metadata and the orphan sweep collects it after
`OrphanSweepMinimumAge` (1 hour). **Delete-after-accept is deliberately not started**, the mission
forbids it while two questions are open with `queue-` (whether the spool roots are shared, and the
measured peak). Pinned by `IngressSinkTests.An_accepted_message_leaves_two_payloads_on_the_spool`, so
the change that removes it has something to fail against.

## The Cloudflare intake, `POST /v1/ingress/cloudflare`

Added at `overview-`'s ruling. `Endpoints/CloudflareIngressEndpoints.cs`, mapped in `Program.cs`
**only when `CloudflareIngress:Enabled`** (a route that exists and always refuses invites a
configuration change to "fix" it; a 404 says the deployment has no such intake).

- **Bearer auth**, secret from `HostCredentials.CloudflareIngressSecretEnvironmentVariable`
  (`STYLOMAIL_CF_INGRESS_SECRET`). There is deliberately **no `SharedSecret` config property**, the
  name is defined once beside the other two and the value is never written down.
- **Enabled without the secret refuses to start**, via the pure
  `HostCredentials.RequireCloudflareIngressSecretIfEnabled(secret, enabled)`, pure so it is testable
  without mutating process environment, which races against the whole suite.
- Raw RFC 5322 body, envelope in `X-StyloMail-Envelope-To` / `-From` headers, tiny JSON reply.
- **Kestrel's body limit is raised to the connector's own maximum for this endpoint** via an endpoint
  filter setting `IHttpMaxRequestBodySizeFeature`. Its 30 MB default is below the connector's 64 MB, so
  without this a message between the two is refused by the server with a 413 that names no component.
- Two gates, both required: the Worker's secret, and a recipient in a served domain. The second is what
  still holds if the first leaks.

**`WebApplicationFactory` runs `TestServer`, which does NOT enforce a request body limit**, so the
suite physically cannot verify that filter. The live probe can, and does: a **32 MB body returns 503,
not 413**. Do not let a future reader "simplify" that filter on the strength of a green suite.

## Assertions added after the wiring landed

- **`The_hop_count_an_ingress_observed_reaches_the_envelope_the_pipeline_sees`**, theory over
  0/7/19. `transport-` flagged that `HopCount = submission.HopCount` was the last link in a chain
  already inert once, with no test reading a count back. **Mutation-verified: deleting that line turns
  all three cases red.** Zero is a case, not the default, the sink *did* look, so null would be a
  false claim about our own behaviour.
- **`AdmissionRefusalMappingTests`**, `overview-` expected a `QueueAdmission` switch in the Host
  needing `RefusedNullSender = 7` added. **There is no such switch**: the Host maps
  `MailAssessment.SubmissionId` + `MailAction` and never reads the queue's admissions, so a new enum
  member cannot change an outcome. Since that is a claim about code shape, the tests drive a real
  `RefusedNullSender` from the real `QueueStore` out through both Host edges and assert neither can
  answer 2xx/250.

## Names that were lies

Two caught in one afternoon, both mine, both the same class as the stale comments and the unfed
`MaxHops`, **a name read as a fact**:

- `AcceptanceCounter.Accepted` counted *calls* to the intake, including refused ones. A new test
  asserted 0 on a run where the queue was called once and refused; the count was right and the name
  was wrong. Renamed `AcceptAttempts`, documented as deliberately counting calls, the double-accept
  defect is two *calls*, so that is the only place the second one is visible.
- `IngressComposition.RequireSharedSpool` compares by reference, which is correct, but a reader could
  take "shared" to mean same-path; the doc says why it must be identity.

## `docs/running.md` — the executable's operating documentation

Written from a **34-check verification script** (`/tmp/stylomail-probe/docs_probe.py`) that starts the
real binary and drives real sockets and HTTP requests. `overview-`'s instruction was to verify each
claim by running, not by reading `Program.cs`, and that is the whole value of the document: a comment
is a claim about code, not evidence about it.

What running changed, versus what reading would have given me:

- **Startup refusals abort with `SIGABRT`** — shell exit `134`, not a tidy `1`. Worth stating, because
  a script checking `$?` sees 134. Five misconfigurations run and each names the fix.
- **Readiness is a probe, not a startup flag** — verified by `chmod 500` on the spool *underneath a
  running process*: `503 {"failedChecks":["spool"]}`, and back to `200` when restored.
- **A CLI command does not start hosted services** — the sharp discriminator is a config that makes
  the SMTP listener's *construction* throw: a command that started it would fail, and `assess`/`profiles`
  exit 0 under it. Deterministic rather than timing-based.
- **`Port: 0`** — confirmed via `lsof` against the process rather than trusting the option.
- **Empty `RecipientDomains` refuses inbound** (`550 5.7.1`), run rather than reasoned.

**One self-correction worth keeping:** my first empty-domains check configured a domain and then
asserted the empty behaviour against it. It failed, and the system was right. Also one transient probe
failure during a rebuild window — re-ran three times on a verified-clean tree, 34/34.

**Still open from `overview-`: the sender-listing route `desktop-` will ask for.** Held deliberately —
nothing is built until the request arrives, and nothing speculative.

## The operator-console read routes (`desktop-`'s ask)

Both built at `desktop-`'s request via `overview-`. `ListingEndpoints.cs` + `Contracts/ListingResponses.cs`,
both `Review`, both tenant-scoped **from the principal with no tenant parameter at all** — a
cross-tenant read is *absent* rather than forbidden.

- **`GET /v1/senders`** — the tenant's configured principals joined with `ISenderControlStore` state
  (new `ListAsync`, one query rather than N). `PrincipalDirectory.ForTenant` added. **The response
  names each field rather than serialising `HostPrincipalOptions`** — that type holds the API key, so
  a serialisation would publish every credential on the host. A test asserts none of the seven test
  keys appears in the body; do not "simplify" it into a serializer.
- **`GET /v1/messages`** — `QueueStore.ListAsync` with `state=awaiting_decision|held|quarantined`,
  `limit`, `after`. Rows are `SubmissionStatusResponse`, the same projection the single-message route
  serves, so a console renders list and detail from one shape.

**`state=queued` is refused with `400 unknown_state`, deliberately.** `QueueListingFilter` enumerates
by *disposition*, not delivery progress, and post-filtering a cut page would produce short pages and a
wrong `hasMore`. A `queued` filter would be a `queue-` change; asked rather than faked.

**OPEN, reported to `overview-`: a privilege asymmetry I inherited.** `GET /v1/submissions/{id}`
requires `Send` while `POST /v1/quarantine/{id}/release` requires `Review` — on the same queue id. A
pure reviewer can release a quarantined message but cannot read it first. It predates this work; the
new listing is what exposed it. I recommended adding `Review` to the submission detail route and did
not change it unilaterally.

## The `GET /v1/submissions/{id}` privilege ruling

`overview-` ruled: **add `Review` to the route**, on the argument that *reading is strictly weaker
than releasing* — `POST /v1/quarantine/{id}/release` (Review) addresses the same queue id, so requiring
the greater capability for the lesser act was incoherent and produced an operator acting on a message
they could not inspect.

- New `HostPolicies.SendOrReview`. **It needed its own policy, not two names on the route:**
  `RequireAuthorization("a", "b")` is an **AND** in ASP.NET Core, so naming both privileges would
  require both and lock out exactly the caller the ruling exists to admit. That trap is in the
  policy's comments.
- The union is documented as **an exception needing justification each time, not a facility**.
- Three tests: a Review-only principal reads a submission it can already release (same id, then the
  release); an assess-only principal still reads nothing; and nothing else widened (a reviewer still
  cannot submit or pause).

## The queue's paging defect — found, fixed, verified in the same hour

`QueueStore.ListAsync` built its next-cursor from **two different rows**: `lastCreatedAt` was
overwritten on every row the reader yielded, so it held the *probe* row's timestamp while the id
paired with it was the last row *kept*. The next page skipped every row whose timestamp fell between
them, and the probe row survived only on a GUID tiebreak.

- **Silent** (fewer rows, `hasMore: false`, no error) and **intermittent** (~half of runs). Both
  properties matter: the first is why a duplicate-only assertion misses it, the second is why it reads
  as a flaky test.
- `queue-` fixed it; **6 failures in 12 runs → 0 in 15** on the same measurement.
- My test's comment said "KNOWN RED, intermittently" and became **false the moment it was fixed** —
  rewritten in the same change. Same failure class as the stale `MailFrom` doc comments.

**The discriminator for real-vs-phantom intermittency — `queue-` sharpened my version, and theirs is
better.** Mine was "systematic vs varying". Theirs names what varies:

> **The signals that were real did not change with the input; the ones that were not real varied with
> an *input*.** Mine and theirs: same test, same property, every time. `access-`'s three retractions:
> the victim changed with *which mutation happened to be live*, and the other two with *payload size*.
> That is a sharper test than "does it look flaky", and it is now the first question to ask.

**AND A NEAR-MISS IN MY OWN MEASUREMENT HARNESS — read this before trusting any loop of mine.**
My gated stress loop counted a `dotnet test` that **never executed** as a pass: the shell cwd had been
left in `.styloagent/channel/inbox` by an earlier `cd`, so the command failed with "project file does
not exist", produced no `Failed!` line, and the loop recorded a green run. A measured "12 clean runs"
was briefly 12 runs of nothing.

The fix, now in every loop: **a run that did not print `Passed!`/`Failed!` is counted as `not executed`
and excluded, not as a pass.** Any loop that counts only failures is blind to its own silence — the
same defect shape as an unfed `MaxHops` or a doc comment nobody re-read.

## `GET /v1/decisions` — the ledger listing

Third console route. `Review`, tenant-scoped from the principal with **no tenant parameter**, keyset-paged,
bounded at 50/100 (lower than the queue's 200 — a ledger row is a whole decision, not a queue row).

- **Rows are summaries**: action, ordered reasons (code *and* message), versions, coverage. Full
  explanation + evidence is one `GET /v1/decisions/{id}` away. A page of complete decisions is
  unbounded because evidence volume is per-message.
- `action` filters by equality on the ledger's own indexed column — it filters the *query*, not the
  page, which is what makes it honest to offer. Unknown action → `400 unknown_action` naming what
  exists. Bad cursor → `400 invalid_cursor`; **treating a bad cursor as "no cursor" would answer page
  one, so a client would loop on page one forever with nothing saying why.**
- The shared `Reasons`/`Versions`/`Coverage` mappings are now single factories used by both the
  listing and the detail, so two projections of one decision cannot drift.
- **Paging is tested with a clock the test controls** (`TestHost.WithClock`). Two decisions in the
  same millisecond tie on the cursor's tiebreak, so a test *meaning* to exercise distinct-timestamp
  paging can quietly become a same-timestamp one and stop covering its own case — exactly how
  `queue-`'s test was blind. **Mutation-verified: reintroducing the queue's cursor bug (probe row's
  timestamp + kept row's id) turns `Paging_returns_every_decision_exactly_once` red, alone.**

## The AirPlay documentation defect, and what it says about "verified"

`docs/running.md` used `127.0.0.1:5000` in its examples. **On macOS that is AirPlay Receiver
(`ControlCenter`), which answers `403`** — so a reader following the doc gets a plausible refusal from
a process that has nothing to do with StyloMail and concludes the host is refusing them. Confirmed with
`lsof`, fixed to `8080` (checked free *and* verified serving), and the hazard is now documented beside
the `lsof` command that settles it.

**The lesson is sharper than the fix.** The document's opening claim — "every claim was checked against
a running process" — was true, and still let this through, because I verified the *claims* and never
the *example the reader actually copies*. "Verified" has to name its scope: **an example is a claim.**

## A rejected provider credential was invisible to readiness (fixed)

The failure the project exists to eliminate: a rotated Jev key made every assessment 500 while
`/health/ready` answered `200 ready`, so a load balancer kept routing mail to a host that could not
assess any of it. The adapter's 401 throw was deliberate and correct; **nothing read it**.

- `ProviderCredentialHealth` — a latching singleton. `CredentialAwareSemanticClassifier` observes the
  exception on its way past and **rethrows unchanged**: the fix was a reader, not a quieter exception.
- `ReadinessProbe` gained a `provider_credential` failed check. **Liveness stays 200** — a rotated key
  is a config fault and a restart fixes nothing, so failing liveness would turn a bad deploy into a
  restart loop.
- **Only a real answer clears the latch.** The reliable signal is `ResolvedModelVersion`, null on
  every failure path and set only when the provider answered. Do **not** use "has evidence" or "has a
  cache object": both are non-empty/null-free even on an `Unavailable` result, which would clear a
  rejection on the very result that proves the key is still bad. `An_unavailable_result_does_not_clear_a_rejection`
  exists because of that near-miss.
- A 422 is deliberately *not* a credential problem — it is our bug, and making the host not-ready for
  it would take a service out of rotation over something a restart cannot fix.

**Verified against a real 401**, both live (endpoint pointed at a refusing stub: ready → 503
`provider_credential`, live → 200) and in-process through the **whole real pipeline** (real adapter,
real decorator, real `AssessmentPipeline.Create`, real `MailAssessor`).

**A mistake worth remembering: my probe lied to me for three rounds.** It pointed the endpoint at a
Python `http.server` stub returning 401, and the host *degraded* instead of rejecting — so I became
convinced the fix was unwired. **The stub was at fault:** Python's `BaseHTTPRequestHandler` defaults to
HTTP/1.0 and answered without reading the request body, so the client saw a connection fault, which the
adapter correctly turns into `Unavailable`. The host was right throughout. Set `protocol_version =
"HTTP/1.1"` and read `Content-Length` bytes before answering. **When a probe disagrees with a passing
unit test, suspect the probe.**

## `JevOptions.Endpoint` and `Model` now bind

They were hardcoded while `IConfiguration` sat in scope as a parameter, so both could be set and were
silently ignored. `HostServices.BuildJevOptions` is public (like `DescribeTransport`) so it is
directly testable. A non-default endpoint logs a **WARNING naming the destination** — it decides who
receives the mail this deployment processes, so the silence was the wrong part rather than the
override. The API key is asserted never to appear in that log.

## The message→decision link

The console's headline use case — "I see a quarantined message, why was it held" — had no path.
`assessmentId` appeared only on the `POST /v1/submissions` response, which a reviewer working from a
list never saw, and the queue carries no assessment id.

- `SubmissionStatusResponse` gains **`internalMessageId`**; `GET /v1/decisions?messageId=` filters on
  it (new index `ix_host_decision_ledger_message`).
- **Chose the read-side route over a queue-row schema change**, which would have needed `queue-` and
  `assess-` too. It also returns a *list*: a message can legitimately be assessed more than once, and
  a single column could hold only one.
- The full chain is tested end to end: submit → quarantine → list → join → fetch evidence, asserting
  the queue id and the decision describe the same message.

## The operator management surface (`desktop-`'s four asks)

Agreed with the operator; design at `docs/console-management-design.md` (theirs, committed).

**Built (asks 1 and 2):** `Controls/OperatorMetadataStore.cs` + `Endpoints/ManagementEndpoints.cs` +
`Contracts/ManagementContracts.cs`; tables `sender_profile` and `company` in `HostDatabase` (additive,
`IF NOT EXISTS`).

- `GET/PUT /v1/senders/{id}/settings` (Review / Administer) and `GET/POST /v1/companies`,
  `PUT /v1/companies/{id}` (Review / Administer). Tenant from the principal, **no tenant parameter**.
- **`GET /v1/senders` rows now carry `label` and `companyId`** (joined from one profile query, not one
  per sender) so the sidebar groups without a request storm.
- **A sender nobody described is `200` with nulls, not `404`** — the principal exists; 404 would read
  as "no such sender". **`PUT` is a full replace, not a merge** — a merge makes clearing a field
  impossible. **`posture` is a closed set**, refused by name otherwise: a stored stance nothing
  recognises looks like a decision someone made. **`updatedBy` comes from the principal, never the
  body.** Unrecognised posture → `400 unknown_posture` naming the valid values.
- **`posture` and `notificationTarget` are stored and read by nothing.** `desktop-` asked for that on
  the record rather than discovered, and the "not yet acted on" wording is in the **API field docs**
  as well as their UI, because a console is not the only client.

**Escalated, not built — ask `overview-` before proceeding:**
- **The key CLI / principal store.** Moves API keys out of plaintext configuration into a digest store
  with `PrincipalDirectory` store-first. Right direction, but it changes the **authentication path for
  both HTTP and SMTP submission**, so it is a credential-model change. Three specifics put to
  `overview-`: two sources of identity (store + env fallback); **`key revoke` against an
  env-configured principal would be a silent no-op** and should refuse instead; printing a key to
  stdout once as the intended channel.
- **The SignalR hub.** The four constraints `desktop-` proposes are right and should be held exactly
  (events are a **hint never state**; live-vs-stale visibly distinguished; **no key in a query string**;
  `wss://` off loopback). It adds a dependency and a transport, and the events would have to be emitted
  from `assess-`'s and `queue-`'s paths — so it is the one item that cannot be done in this lane alone.
  Lands last.

## RULING RECEIVED — the key CLI and the hub are both approved (guards below)

`overview-` ruled on both, 2026-09-22. Neither direction reopened; these are the guards that must
ship with them. **The key CLI has NOT been started** — see the note at the end.

### 1. Minted API keys — approved, five guards

`HostPrincipalOptions.Key` plaintext in configuration goes; a store holds digests. **All five are
requirements, not preferences:**

- **(a) Precedence is total, never merged.** Store first, environment as fallback, and where a
  principal exists in both the store entry wins **wholesale** — never union privileges, never union
  keys. A union lets an environment entry silently re-widen a privilege the operator deliberately
  narrowed.
- **(b) Two sources is acceptable only because it is visible.** `key list` must report, per principal,
  whether `store` or `environment` resolved it. Freeze environment principals **read-only** — not
  editable, not revocable, from CLI or console. Do **not** schedule a deprecation; revisit when there
  is a second deployment.
- **(c) `key revoke` refuses on an environment principal and names the configuration that owns it.** A
  refusal that does not say where to go is a no-op with better manners.
- **(d) The digest is a slow KDF, not a bare hash.** Per-key salt, iterated or memory-hard,
  constant-time comparison. A single SHA-256 is offline-crackable from the store — a store of
  crackable digests is a plaintext store with extra steps.
- **(e) Revocation takes effect immediately.** If `PrincipalDirectory` caches resolutions, state the
  cache lifetime in the design and make `revoke` evict. A revocation a cache serves past its moment is
  guard (c) one layer down.
- **Printing:** `key create` prints **once, to stdout, and to nothing else** — no `--output`, no file
  flag, never a log, never stderr — plus a line saying it will not be shown again.

### 2. The hub — approved, last, default off

Four rules adopted as written, with the third (no key in a query string) and fourth (`wss://` off
loopback) as **hard rules**. The two that decide whether it is a feature: **events are a hint never
state** (the console re-reads the row over HTTP), and **live is visibly different from stale**.

- **SignalR enters behind a configuration flag, default off.** A deployment that has not enabled it
  loses immediacy and nothing else.
- **THE HARD RULE: no pipeline code may depend on the hub.** Emission is fire-and-forget; an emission
  that can throw into an assessment or a delivery is wrong regardless of how the events are shaped. A
  hub outage must be invisible to mail flow.
- **Emit at my own boundary first** — the Host's ledger writes, the listing routes, the delivery-worker
  hosting — before asking `assess-` or `queue-` for anything. Where a change is genuinely produced in
  another lane, ask for **one call at its completion boundary and nothing more**. The hub must not
  become a reason for two lanes to know about each other.

### Why the key CLI is not started

It changes the **authentication path for both HTTP and SMTP submission**, and it has to land whole:
a half-built credential path is worse than none, because a partially-working authentication change is
a hole rather than a gap. With context pressure flagged and the guards above being five requirements
rather than one, this belongs in a **fresh context**, started from this section. Everything else is
landed and green, so stopping here is clean rather than a degraded handoff.

## HANDOFF — the hub, and where emission belongs in this lane

`overview-` ruled the hub approved last, flag-off, and that **emission belongs at my boundary before
anyone else is asked for a hook**. I am handing it off rather than half-building it (same reasoning as
the key CLI, and `overview-` offered the choice explicitly). Whoever picks it up needs these facts,
because they are the part only this lane knows.

**The console's four event types and where each is produced, all inside this lane:**

| Event | Produced at |
| --- | --- |
| assessment completed (id, action, risk, message id) | `SqliteDecisionLedger.RecordAsync` — the ledger write is the completion boundary, immediately after `AssessmentsEndpoints` / `SubmissionsEndpoints` call it |
| message state changed | `QueueDeliveryHostedService` (hosting is mine; the state transaction itself is `queue-`'s) and `QuarantineEndpoints` |
| sender paused / resumed | `ControlsEndpoints.PauseSenderAsync` / `ResumeSenderAsync` |
| readiness changed | `ReadinessProbe.Check` / `ProviderCredentialHealth` — a transition, not every poll |

**The hard rule, first and above everything:** no pipeline code may depend on the hub. Emission is
fire-and-forget and **must not be able to throw into an assessment or a delivery** — a hub outage is
invisible to mail flow. The shape that satisfies it: an `ITrafficEvents` port with a no-op default
implementation (the flag-off path), and every implementation wrapping its own body so nothing escapes.
Do **not** call a hub context directly from an endpoint or a worker.

**If a change is genuinely produced inside another lane**, ask that lane for **one call at its
completion boundary and nothing more**. The hub must not become a reason for two lanes to know about
each other.

**Auth:** the key never goes in a query string. Header on **both** the negotiate request and the
WebSocket handshake — SignalR's usual `access_token` pattern puts the credential in the URL, where it
lands in access logs, proxies and crash reports. `wss://` for anything not loopback.

## Follow-up work landed after the first report

- **CS0168 cleared** (`SubmissionsEndpoints.cs`). `overview-` asked whether the discarded exception
  was an oversight or deliberate; it is deliberate, and now says so. `SpoolUnavailableException`
  comes from the queue's spool and its messages name the **spool directory, the errno and the queue
  item id**, returning that to `POST /v1/submissions` would disclose internal paths and identifiers.
  The sibling handler that *does* surface a message uses the host's own `StorageUnavailableException`,
  which carries a fixed sentence. Now an unbound `catch` with the reasoning in a comment, so the
  asymmetry reads as intended rather than as sloppiness. `overview-` was also asked whether to turn
  on `TreatWarningsAsErrors` repo-wide, clean today, so cheap now.
- **`ClosedPort()` TOCTOU removed** (`DeliveryWorkerHostingTests`). A probe-then-release port helper:
  between `Stop()` and the host's later bind, the OS may hand the port to anyone, including a parallel
  test class. Flagged by `access-` and `assess-`. Fixed by **removing the need for it** rather than
  narrowing the window: the upstream target is now `192.0.2.1` (TEST-NET-1, RFC 5737), never assigned
  and never routable, so no port is allocated at all. A race that cannot fire today is one that fires
  the day the test acquires a reason to dial.

## Deliberately not done

1. ~~No HTTP intake route for the Cloudflare connector~~, **done**, at `overview-`'s ruling. See the
   Cloudflare section above.
2. **`JevOptions.Endpoint` is still not host-configurable.** Left alone because a config key that can
   redirect message content is an operator privacy decision, not a wiring gap. It is what blocks a
   fully live end-to-end acceptance probe against a local stub. Suggested to `overview-`.
3. **No `SmtpSubmissionListener`/`SpoolStore`/`QueueStore` changes**, none are mine.

## Infra gotchas learned the hard way

- **The host's composition root runs before `WebApplicationFactory` layers test config in.** Anything
  that decides from `IConfiguration` at `AddStyloMailHost` time reads the wrong values. This is why
  `SmtpIngress:Enabled` is honoured in `SmtpIngressHostedService.StartAsync` and not at registration,
  and why the transport options are resolved through `IOptions<>` inside factories.
- **The Host now references `StyloMail.Transport`** (added to `StyloMail.Host.csproj`). Transport does
  not reference Host; no cycle.
- **`X509CertificateLoader.LoadPkcs12FromFile`**, not the obsolete `X509Certificate2` constructor,   the old form is a build error under this repo's analyzers. `DefaultKeySet`, not `EphemeralKeySet`
  (the latter cannot be used by `SslStream` for server auth on every platform).
- **A test that passes against a fake can still be green over a broken seam.** Every ingress test runs
  against `RecordingAssessor`, which never runs the pipeline's step one, so `IngressPipelineSeamTests`
  exists to run the *real* `AssessmentValidation` over the exact input the sink builds. Mutation-verified:
  setting `ParserLimitExceeded = true` in the placeholder turns it red.
- **`TestServer` does not enforce a request body limit.** Anything about request sizes, Kestrel limits
  or server-level refusal is invisible to the suite and has to be probed against a real process.
- **A red test in a shared suite is indistinguishable from a lane's breakage.** When asked to write a
  test that is expected to fail, make the failure self-describing, catch, and `Assert.Fail` with the
  defect, the owning lane and the word EXPECTED, because three agents lost part of an afternoon this
  way. Delete the scaffolding once it goes green.
- **Inverting a characterisation test beats deleting it.** A test that pins components *agreeing* is a
  guard; remarks recording that they once disagreed are history. I deleted one and was told twice to
  invert it; they were right.
- **`dotnet test --no-build` after editing a source file tests the previous mutation.** Cost me a
  confusing 5-of-10 red run that was really the mutation still on disk.
- Judge this work with `dotnet test tests/StyloMail.Host.Tests/…`, never the solution build,   several agents edit `StyloMail.slnx` concurrently.

## Config surface added (`StyloMail:Transport`)

`SmtpIngress:{Enabled,BindAddress,Port,ServerName,CertificatePath,CertificatePassword,RequireEncryption,
AllowUnauthenticatedInbound,RecipientDomains,InboundTenantId,LocalHostIdentities,MaxMessageBytes}` ·
`CloudflareIngress:{Enabled,RecipientDomains,InboundTenantId,ConnectorId,LocalHostIdentities,
ByHost,MaxMessageBytes}` · `Upstream:{Host,Port,Tls,ImplicitTls,Username,Password,HeloName}`.
Also `StyloMail:Auth:Principals:<n>:ApprovedSenderIdentities:<n>`, **empty authorises nothing** beyond
the null sender. The Cloudflare secret has **no config key by design**; it is
`STYLOMAIL_CF_INGRESS_SECRET` only, see `HostCredentials`.

Everything defaults to off. `RequireEncryption` defaults true, so enabling the listener with no
certificate refuses to start, deliberate, and the transport says why with its own message.

## Hard rules I hold

- **Only the assessor accepts.** Never call `QueueStore.AcceptAsync` from the ingress path.
- **A 250 requires a durable queue id.** `Allow` with no `SubmissionId` is a deferral, not an ack.
- Secrets come only from `TYPESAFE_API_KEY` and `STYLOMAIL_PROFILE_KEY`; `jevkey.pvt` is not a source.
- No internal prose in an SMTP reply, reason *codes* only.
- Do not start delete-after-accept until `queue-` answers the two open questions.
