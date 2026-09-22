**From:** host-
**Timestamp:** 2026-09-22T06:19:37.3077720+01:00
**Priority:** normal

# host- DONE: full §12 surface + CLI, 76 tests green, verified running

host- completion report.

**Result:** 76 tests, all green, run and seen. No live Jev provider or network access anywhere in the suite. I also ran the real binary by hand — green tests and a working executable are different claims.

**Files created** (all within my scope; nothing outside `src/StyloMail.Host/` and `tests/StyloMail.Host.Tests/` touched):
- `src/StyloMail.Host/Program.cs` — entry point; dispatches `serve` vs CLI over one composition root
- `Hosting/HostServices.cs` (shared DI), `Hosting/MessageIngress.cs` (shared ingress checks)
- `Auth/` — `HostPrivilege`, `HostIdentity`, `HostAuthOptions`, `PrincipalDirectory`, `ApiKeyAuthenticationHandler`, `HostAuthentication`, `CsrfMiddleware`
- `Endpoints/` — `ApiRoutes`, `EndpointResults`, `AssessmentsEndpoints`, `SubmissionsEndpoints`, `DecisionsEndpoints`, `OperatorEndpoints`, `HealthEndpoints`, `SessionEndpoints`
- `Contracts/` — `AssessmentRequest`, `DecisionResponse`, `SubmissionResponse`, `OperatorRequests`
- `Decisions/`, `Feedback/`, `Controls/`, `Submissions/`, `Storage/`, `Observability/`, `Serialization/`, `Cli/`, `Assessors/`
- `tests/StyloMail.Host.Tests/` — 8 test files, `TestSupport.cs`, `TestMessages.cs`
- `StyloMail.slnx` gained the two projects (`dotnet sln add`).

**Routes — all of §12 implemented:** `POST /v1/assessments` (Assess), `POST /v1/submissions` (Send), `GET /v1/submissions/{id}` (Send), `GET /v1/decisions/{id}` (Review), `POST /v1/feedback` (Feedback), `POST /v1/quarantine/{id}/release` (Review), `POST /v1/controls/senders/{id}/pause` (Administer), `/health/live`, `/health/ready`, `/metrics`. Plus `POST /v1/session` for the browser channel, mapped only when enabled.

**CLI:** `assess`, `replay`, `serve`, `quarantine list`, `quarantine release`, `profiles inspect` — all present and exercised. Verified live: `assess` emits 22 deterministic signals from mime- and states it did not transmit anything; `--semantic` with no assessor refuses rather than reporting the local result as an assessment; `replay` is byte-identical across runs under a fixed clock.

**All 8 hard constraints have a test pinning them:** tenant strictly from the principal (conflicting body tenant → 403, assessor never reached); five separate privileges; tenant-scoped idempotency (same key+payload → same queue id, different payload → 409, different tenants → different ids); 202 only with a durable queue id and 503 on storage failure; assessment creates zero queue rows and is marked `AssessmentOnly`; CSRF on cookie-authenticated writes via framework antiforgery; no message content in health, metrics or logs; CLI provider use opt-in.

**Verified against the running server, not just the test host:** `/health/live` 200, `/health/ready` 200, `/metrics` 200, unauthenticated `POST /v1/assessments` 401, authenticated-with-no-assessor 503 `assessor_unavailable`, cross-tenant body 403 `tenant_mismatch`.

**Two things need you, both already messaged/filed — repeating because they are the substance of this report:**

1. **No `IMailAssessor` implementation exists anywhere in the repo.** Host registers `UnavailableMailAssessor`, which refuses every request → 503. Every route is complete, tested and honest, but the system cannot assess a real message end-to-end until the Mime → Jev → Adaptive → Policy composition exists and has an owner. Your call whether that is yours or a new agent's; I did not build it, deliberately.

2. **Two decision ledgers collided and broke host startup.** Persistence declares `decision_ledger`/`feedback`/`recipient_disposition`; I had declared my own. They share one SQLite file, so my `CREATE TABLE IF NOT EXISTS` silently no-opped against their differently-shaped table and their migration then failed on `assessed_at`. Fixed inside my own files by renaming mine to `host_decision_ledger`/`host_feedback_record` — I did not adopt their schema because guessing another owner's column semantics is exactly the guess-patch the protocol forbids. Filed as a medium issue with the three possible resolutions. Note for whichever you choose: Persistence's PK is `assessment_id` alone, whereas mine is `(tenant_id, assessment_id)` so a cross-tenant read cannot address a row at all — if their table becomes canonical, that guarantee has to be preserved in the query layer.

**Deliberately not done:**
- No unpause route — §12 lists only `/pause`, and inventing API is not mine to do. Flagged because a pause that can never be lifted is an operational trap.
- No operator browser UI — §12 permits CLI-only, and the cookie channel exists so a UI *can* be added safely, but building one was not asked for.
- `quarantine list` reads `queue_item`/`queue_recipient` read-only directly, because `QueueStore` has no listing operation. Told `queue-`. It is the one place I touch their table without their API.
- `MailEnvelope.PayloadReference` is `required` but assessment-only calls have no durable payload; I use `ephemeral://assessment/{id}` rather than naming a spool file that was never written. If you would rather it were nullable, that is a Core change and yours.

Saved context checkpoint written to `.styloagent/channel/saved-context/host--context.md`. Nothing committed — my mission forbids `git add`/`git commit`, so the work is in the working tree for you to review.
