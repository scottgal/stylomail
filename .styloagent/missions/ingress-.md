# `ingress-`, finish the Host composition wiring

You are taking over the remaining `src/StyloMail.Host/` work from `host-`, which completed the host,
the CLI, the assessor wiring and 94 green tests, then stopped cleanly on budget rather than pushing
through. **Its checkpoint is written for you:** `.styloagent/channel/saved-context/host--context.md`.
Read it before anything else.

**Own** `src/StyloMail.Host/` and `tests/StyloMail.Host.Tests/`. Do not modify other projects.

## The four remaining items

### 1. `ISmtpIngressSink`, the main deliverable
`transport-` has two production entry points ready and **neither is usable until this exists**:
- `SmtpSubmissionListener(options, ISmtpIngressSink, ISubmissionAuthenticator?)`
- `CloudflareEmailRoutingConnector(options, ISmtpIngressSink)`

They take the same sink. The sink must:
1. translate inbound bytes + envelope into a `MailAnalysisInput`;
2. call `IMailAssessor.AssessAsync` with `AssessmentOnly = false` and the client idempotency key
   (**absent for MTA/Cloudflare ingress, that is expected, not a bug**);
3. read `MailAssessment.SubmissionId` and `MailAssessment.Submission` for the outcome.

> **CRITICAL, it must NOT call `QueueStore.AcceptAsync`.** The assessor already accepts. A sink that
> "runs the pipeline, then accepts" accepts **twice under two different keys**, the queue cannot
> dedupe, and **the message is delivered twice.** Get this wrong and you reintroduce the exact defect
> that took most of this session to remove. `host-` recorded this trap explicitly; it was in
> `transport-`'s original sketch and reads as obviously correct.

### 2. Construct both listeners
In `HostServices`/`Program.cs`, alongside `MailAssessor`.

### 3. Host the delivery worker
`QueueDeliveryWorker.RunAsync` exists, is tested, and **has no caller**. Register it as a **hosted
service** in-process with the Host, passing the shutdown token so its bounded drain works.

### 4. Two composition assertions, make them fail loudly, do not merely document
- **`transportMax <= queueMax`**: assert `SmtpIngressOptions.MaxMessageBytes <=
  QueueOptions.MaxPayloadBytes` at construction, naming both values on failure. If they drift the
  ingress accepts, the sink spools, the queue refuses, and the caller sees a capacity deferral that
  looks like spool pressure while the cause is two components away.
- **One `SpoolStore` instance.** The sink must write through **the same instance** the composition
  root passes to `AssessmentPipeline.Create(...)`. `transport-` has no spool at all by design, so a
  sink that constructs its own makes delete-after-accept, the orphan sweeper and the assessor's
  read-back reason about **different directories**, each correct alone, broken together, silent.
  Do not let the sink `new` a spool.

## Hard constraints
- **Secrets come only from environment variables**: `TYPESAFE_API_KEY` and `STYLOMAIL_PROFILE_KEY`
  (already implemented in `HostCredentials`, reuse it, do not invent names).
- **`jevkey.pvt` is off-limits.** Never read, print, reference, or document it as a source.
- Keep `UnavailableMailAssessor` as the fallback for unconfigured deployments, it is correct
  behaviour, not a placeholder.
- **Do not start the spool deletion** (`host-`'s delete-after-accept). Two questions are still open
  with `queue-`: whether the spool roots are shared, and the measured peak. Do not assume either.

## Method, this matters more than speed
`host-`'s observation, worth inheriting: *almost every defect it found in its own lane came from
**running** the thing rather than reading it back*, the lazy singleton that booted healthy while
half-configured, the 422-vs-500 probe that proved DI actually constructed the assessor, a mutation
that showed its own test fix was cosmetic. **Run the host. Probe it. Do not trust a clean build.**

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- **Several agents edit `StyloMail.slnx` concurrently.** Judge your work with
  `dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj`, never the solution build,   watching the solution is `overview-`'s job.
- Analyzers are **errors** here (`CA*`/`IDE*` fail the build).
- No `git add`/`commit`.

## Done when
Your tests are green, you have **run the host and probed it**, and the ingress paths work end to
end from an external source. Report to `overview-` with: files created, test count, what you probed
and what it returned, contract friction, and anything deliberately not done. **If blocked,
`send_message` immediately, do not yield silently.**