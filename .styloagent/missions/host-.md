# `host-`, HTTP host, CLI and operator surface

## Your scope
Own `src/StyloMail.Host/` (web project) and `tests/StyloMail.Host.Tests/`. Nothing else.
Do **not** modify `src/StyloMail.Core/`, `src/StyloMail.Persistence/`, `src/StyloMail.Jev/`,
`src/StyloMail.Mime/`, `src/StyloMail.Adaptive/`, `src/StyloMail.Policy/`, `src/StyloMail.Queue/`,
`.styloagent/spec.md`, or `email-proxy-spec.md`. Other agents own those. If a Core contract must
change, `send_message` to `overview-` instead of editing it.

## Read first
- `.styloagent/spec.md`, §2 users, §4 constraints, §5 pipeline.
- `.styloagent/architecture.md`, where the Host sits and what it may depend on.
- `email-proxy-spec.md` (repo root), **§12 "Host API and operator surface" is your detailed brief**,
  and §13 lists the verification scenarios. This is the specification of record.
- `src/StyloMail.Core/`, `IMailAssessor`, `AssessmentContext`, `MailAction`, `MailAssessment`,
  `RecipientDisposition`, `DeliveryState`, `MailDirection`.
- `src/StyloMail.Queue/`, `QueueStore`/`SpoolStore` for the submission path (another agent is
  actively editing this; depend on the types that exist, and `send_message` to `queue-` if you need
  something it does not expose).

## What to build
An ASP.NET Core host exposing the tenant-scoped, authenticated routes from source spec §12:
- `POST /v1/assessments`, MIME plus envelope context; returns an assessment with **no delivery and
  no learning**.
- `POST /v1/submissions`, idempotent durable intake; returns a queue id and assessment/status.
- `GET /v1/submissions/{id}`, recipient-level disposition and delivery progress.
- `GET /v1/decisions/{id}`, evidence, reasons, versions and coverage.
- `POST /v1/feedback`, authorised label/correction with an explicit scope.
- `POST /v1/quarantine/{id}/release`, audited, idempotent release.
- `POST /v1/controls/senders/{id}/pause`, pause an authenticated principal's outbound delivery.
- `/health/live`, `/health/ready`, `/metrics`, **no sensitive content**.

Plus a CLI: `assess message.eml`, `replay fixtures/`, `serve`, `quarantine list`,
`quarantine release`, `profiles inspect`.

## Hard constraints, these are security requirements
1. **Never derive tenant authority from a request body field alone.** Tenant comes from the
   authenticated principal, not from JSON.
2. **Separate privileges** for assessment, sending, review, feedback and administration. A sender
   must not be able to release their own quarantine.
3. **Idempotency keys are tenant-scoped.** A retry with the same key and payload returns the existing
   submission; the same key with a *different* payload is a conflict, not an overwrite.
4. **HTTP `202`/success on submission requires a durable queue id.** If durable storage is
   unavailable, return a temporary failure, never a success that loses mail.
5. **Assessment success has no delivery implication.** Do not let an assessment call send mail,
   advance delivery state, or feed live traffic accounting.
6. **Protect browser-based controls against CSRF.**
7. **No request bodies in logs.** Email content is personal data. Health and metrics must not expose
   it either.
8. **CLI assessment must not silently transmit private content externally**, provider usage follows
   explicit configuration.

## Testing
Use `WebApplicationFactory` (add `Microsoft.AspNetCore.Mvc.Testing`). Cover at minimum:
unauthenticated request rejected; cross-tenant access to another tenant's decision denied; a
submission retried with the same idempotency key and payload returns the same queue id; the same key
with a different payload conflicts; submission fails closed when the queue is unavailable; a sender
cannot release a quarantine; health and metrics leak no message content; assessment does not create
queue state.

Do not require a live Jev provider or network access in tests.

## Build notes
- `dotnet` is NOT on PATH:
  `export DOTNET_ROOT=/usr/local/share/dotnet && export PATH="/usr/local/share/dotnet:$PATH"`
- SDK 10.0.201. Use `Microsoft.NET.Sdk.Web`, TargetFramework `net10.0`.
  **Other agents may edit `StyloMail.slnx` concurrently**, if `dotnet sln add` fails, retry once;
  always verify with `dotnet test tests/StyloMail.Host.Tests/StyloMail.Host.Tests.csproj`.
- Do not run `git add` or `git commit`. **Never read, print, or reference `jevkey.pvt`.**

## Done when
Your tests are green and you have seen them run. Report to `overview-` via `send_message`: files
created, test count, routes implemented, contract friction, anything deliberately not done.