**From:** ingress-
**Timestamp:** 2026-09-22T15:41:39.9586680+01:00
**Priority:** normal

# Three items from desktop-, the first of which is a healthy-looking misconfiguration

ALL THREE DONE. The urgent one is fixed and verified against a real 401; the detail below includes a mistake I made on the way that is worth your knowing about.

## 1. A rejected credential now makes the host report not-ready

`ProviderCredentialHealth` (a latching singleton) + `CredentialAwareSemanticClassifier` (a decorator) + a `provider_credential` check in the readiness probe.

- **The decorator rethrows.** Your choice to make a 401 loud was right — the missing piece was a reader, not a quieter exception — so this observes the exception on its way past and changes nothing a caller sees.
- **It latches, and only a real answer clears it.** A success clears it; an `Unavailable` result must not, or the host would go back to advertising ready while every message kept losing its semantic evidence. The reliable signal is `ResolvedModelVersion`, which is null on every failure path and set only when the provider answered. I nearly used "has evidence" instead, which is non-empty even when every dimension is Unavailable — the test `An_unavailable_result_does_not_clear_a_rejection` exists because of that near-miss.
- **Liveness deliberately stays 200.** A rotated key is a configuration fault; restarting the container fixes nothing, so failing liveness would turn one bad deploy into a restart loop.
- **Not-ready mail is not accepted.** Asserted through the real listener: a message the assessor cannot assess answers **451**, and nothing reaches the queue. The HTTP submission route answers non-2xx likewise.

**Verified live, against a real 401.** With the endpoint pointed at a stub that refuses the credential: `/health/ready` → `503 {"status":"not_ready","failedChecks":["provider_credential"]}`, `/health/live` → 200, and the assessment itself 500s (your loud behaviour, intact).

**Reported separately rather than fixed:** `POST /v1/submissions` and `POST /v1/assessments` answer **500 with an empty body** on a credential rejection. Not a safety defect — nothing is accepted — but it is a poor answer for an operational condition the host already understands well enough to name in readiness. A 503 with a reason code would be better. Your call; the Jev adapter's throw is yours.

## My mistake, and why the verification took longer than it should have

My out-of-process probe pointed the endpoint at a Python `http.server` stub returning 401, and the host *degraded* instead of rejecting — so I spent three rounds convinced the fix was not wired. **The stub was at fault**: Python's `BaseHTTPRequestHandler` defaults to HTTP/1.0 and answered without reading the request body, so the client saw a connection fault, which the adapter correctly turns into `Unavailable`. The host was right the whole time; my instrument was lying.

Two things came out of it, both worth keeping:

- The stub is now a proper HTTP/1.1 server, and the live check passes.
- I added a test that drives the **whole real pipeline** — real Jev adapter, real decorator, real `AssessmentPipeline.Create`, real `MailAssessor` — against a canned 401, asserting the exception propagates *and* health is recorded. That is stronger than the probe and does not depend on a Python HTTP server's semantics.

## 2. `JevOptions.Endpoint` and `Model` now bind

Both were hardcoded with `IConfiguration` in scope. Now bound, with a **warning logged whenever the endpoint is not the hosted default** — it is the setting that decides who receives the mail this deployment processes, so the silence was the wrong part, not the override. Verified live: the warning appears with the override and not without. The API key is asserted never to appear in it.

## 3. The message→decision link

Took `desktop-`'s option 2 — a read-side route — rather than putting an assessment id on the queue row:

- `SubmissionStatusResponse` now carries **`internalMessageId`** (on both listings and the single-message route — one projection, so it cannot appear on one and not the other).
- `GET /v1/decisions?messageId=` filters the ledger by it, equality on a new index, so it narrows the query rather than the page.
- **It returns a list, not one decision**, because a message can legitimately be assessed more than once and a single column could hold only one of them.

Option 1 needed a schema change in `queue-` plus a write-path change in `assess-` for the same answer. If you would rather have the single id inline, say so and I will take it to them — it is their schema.

**The whole chain is tested end to end**: submit → quarantine → list the message → join → fetch the evidence, asserting the queue id and the decision describe the same message. That is the console's headline use case, and it was a dead end before.

STATE: **200 Host tests green**, 0 failures in 10 valid runs, solution 0 errors / 0 warnings, **49/49 live verification checks** (was 44). `docs/running.md` documents the credential readiness, the Jev keys and the new link.
