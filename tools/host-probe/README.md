# host-probe: the live acceptance run for the Host's management surface

`probe.py` starts a real Host against a real local decision model, drives the REST surface over real
HTTP, and subscribes to the live-traffic hub over a real WebSocket. It is the acceptance proof that
the suite deliberately cannot be.

## Why it is not a test

Every test in `tests/StyloMail.Host.Tests` points the assessment provider at a loopback port that
refuses the connection. That is on purpose: a green suite has to mean the same thing on a laptop, on
CI and in a container, and a suite that needs a model server is a suite that fails for reasons that
have nothing to do with the code.

The cost of that choice is that nothing in the suite ever sees a semantic value. A fake assessor
returns what the test told it to return, so the suite can prove the wiring, the policy and the
persistence, and none of it proves that a model's opinion reaches a decision.

So this probe is the other half. It runs the same composition the deployment runs, with the provider
actually serving, and asserts the whole path end to end.

## What it proves

Run against a Nimble-backed Host on 2026-09-30, 14 of 14 checks passed, and again at
2026-09-30T23:41Z after the run root moved into this lane. The artifact for the second run is
`.styloagent/scratch/ingress/host-probe/run-20260930T234121Z/host.log`; the earlier run's log is the
one copied into `run-20260930T225933Z/` with its provenance beside it. The checks, and what each one
establishes:

| Check | What it establishes |
| --- | --- |
| `host_started_ready` | The Host reaches `/health/ready` `status: ready` with **no third-party credential**: the local provider needs the profile master key alone |
| `liveness` | `/health/live` answers 200 |
| `assess_http_200` | `POST /v1/assessments` returns 200, one live model call (26.3s, 37.3s and 39.1s across runs: the elapsed time is the model's, not a fixed cost) |
| `assess_action` | The response carries an `action` (`Hold` for the fixture) |
| `semantic_rows_present` | 12 semantic rows come back on the evidence |
| `semantic_value_from_the_live_model` | 11 of 12 rows are `Available` with a real value: the model's opinion reached the decision |
| `decision_persisted_and_matches` | `GET /v1/decisions/{assessmentId}` returns the same decision: it was recorded, not just computed |
| `submit_accepted` | `POST /v1/submissions` (with `Idempotency-Key`) returns 202: accepted for asynchronous work |
| `submission_read_back` | `GET /v1/submissions/{queueId}` reads the queue entry back |
| `queue_reached_a_new_decision` | The worker actually assessed the submitted message: a **new** decision appeared afterwards |
| `hub_refuses_an_unkeyed_negotiate` | `POST /v1/traffic/negotiate` without the key answers 401: the feed is not open to anyone who can reach the port |
| `hub_subscription_authenticated` | Negotiate with the key, then connect the WebSocket **with the same key in the header on both**: the subscription is established, and the protocol handshake is acknowledged before anything is sent |
| `hub_announced_the_recorded_decision` | A `DecisionRecorded` notice arrives whose `subjectId` is the assessment id this run just created: the ledger write published, the hub addressed the tenant's group, and this subscription received it |
| `hub_announced_the_queues_decision` | The same, for the decision the **queue's worker** produced rather than a request, keyed on the `assessmentId` from the submission response |

The notice arrives as the documented three fields and nothing else, measured:
`{"kind": "DecisionRecorded", "subjectId": "asm_1634230b…", "occurredAt": "2026-09-30T22:55:55.747981+00:00"}`.
The kind travels as a **name**, never a number, and there is **no tenant field**: the group is the
routing decision, so publishing it would restate what the address already settled. A client gets what
changed and which thing to re-read, never what the change was.

The subscription is spoken by hand (SignalR's JSON protocol over a plain WebSocket) rather than with
the console's own client library, so what is proven is the wire: the credential travels in a header
on the negotiate request and again on the handshake and never in the URL, and a subscription that was
never established cannot be mistaken for a quiet feed.

The fixture is a credential phish ("your account will be deactivated within 24 hours, confirm your
password and card number"). Against the live model the firing dimensions were
`credential_request`, `sensitive_data_request`, `identity_authority_claim`, `urgency_pressure`,
`link_lure` and `threat_reward_inducement` at 1, with `payment_redirection`, `secrecy_bypass`,
`attachment_lure`, `transactional_character` and `unsolicited_solicitation` at 0.

The one `NotApplicable` row was conversational continuity, because the sink supplies no context to
the assessor. That is not a defect in the probe: it is the measured shape of a gap on the ingest
path, and it is why the conversation store has to exist before the dimension can fire.

## What it does not prove

- **Only seven routes.** This probe touches assessments, submissions (write and read), decisions
  (list and read), messages and health. It does not exercise the sender settings, company, feedback,
  quarantine-release or control routes. Those are covered by the suite, not here.
- **`Hold`, not `Quarantine`.** One fixture reaches `Hold`. A quarantine index needs a message whose
  firing dimensions carry enough weight: quarantine is reachable by such a message, and unreachable
  by accumulated history alone. Do not cite this probe as evidence for the quarantine state either
  way.
- **No delivery.** The upstream is `192.0.2.1:25` (TEST-NET-1, never routable) so the worker runs
  without dialling anything real. Whether a delivery lands is not what this measures.
- **No tier 3.** It needs a baseline freeze no production caller can set.
- **One subscription, one tenant, one kind of change.** The hub phase proves a `DecisionRecorded`
  notice reaches an authenticated subscriber. It does not assert `MessageStateChanged`,
  `SenderControlChanged` or `ReadinessChanged` (their kinds are printed if they arrive, but nothing
  fails when they do not), it does not prove tenant isolation by subscribing as a second tenant, and
  it does not exercise reconnection, a dropped feed or `wss://`. A single run of a single fixture.
- **Not a rule about the client.** The hub's own rules for the console (never render a notice as
  state, show live and stale differently, `wss://` off loopback) are the console's to hold and are
  not measured here.

## Running it

Requirements:

- a built Host: `dotnet build src/StyloMail.Host/StyloMail.Host.csproj`
- a local model serving on `http://127.0.0.1:11435` under the `nimble:latest` tag (Ollama)
- python 3 with `websocket-client` installed (`pip3 install websocket-client`), and `dotnet` on
  `PATH` for the child process

```sh
export DOTNET_ROOT=/usr/local/share/dotnet
export PATH="/usr/local/share/dotnet:$PATH"
python3 tools/host-probe/probe.py
```

Optional overrides, all read from the environment: `PROBE_ROOT` (run data, default
`.styloagent/scratch/ingress/host-probe/`), `PROBE_PORT` (default 5399), `PROBE_OLLAMA`,
`PROBE_MODEL`.

The exit code is 0 only if every check passed. The script prints one `CHECK <name>: PASS|FAIL` line
per check, an `OBSERVE` line for each fact it could only learn by running (the field an id lives in,
the listing shape), and a final `SUMMARY n/m passed`.

It also prints `SHUTDOWN exit=N after SIGTERM`, which is **how the host died**, and that is
deliberately not a side note: a graceful stop runs the delivery worker's bounded drain, and nothing
else in this probe reaches that path. The wait is longer than the drain window (30s by default), so a
host that had to be SIGKILLed says so instead of being killed quietly, which is what an earlier
20-second wait did. It is reported rather than asserted: nothing here fails the probe for a slow
drain, because the bound on that belongs to the worker, not to this run.

Run data lands in `$PROBE_ROOT/run-<UTC stamp>/`, printed as a `RUN ROOT:` line at the start of the
run: `host.log` and `host.db` are kept for inspection afterwards. The default root is inside this
lane's gitignored scratch (`.styloagent/scratch/ingress/`), not `/tmp`, because the log behind a
number quoted from a run has to outlive the run and be readable by whoever wants to re-check it. A
directory per run is also what makes that safe: the fresh database a run needs comes from a fresh
directory, so nothing has to be cleared and the previous run's evidence stays where it is.

Before the log is left behind, the hub's connection token is taken out of it and the run prints
`NOTE redacted N connection token(s) in <path>`. The host logs the WebSocket request, and the token
rides in that request's query string: per-connection and dead once the host has stopped, but the log
outlives the socket and gets copied, so it does not travel. Only the hub's own `?id=` is rewritten,
so a query parameter named `id` on any other route stays as logged.

## Credentials

The probe generates the profile master key and the probe principal's key itself, writes each to a
`0600` file under the private run root, and reads them once into the child's environment. Neither
value is printed, echoed, or placed on a command line, and both files are removed when the run ends.
The run root sits under this lane's gitignored scratch, so neither file is reachable by a commit.
The child environment is scrubbed of `TYPESAFE_API_KEY`, `STYLOMAIL_PROFILE_KEY` and
`STYLOMAIL_PROFILE_KEY_FILE` first, so the probe does not inherit a real key from the operator's
shell. Nothing here reads a production credential, and nothing leaves the machine.

The subscription is authenticated by that same generated principal key, in a header on the negotiate
request and again on the WebSocket handshake, never in the URL. The probe also measures the other
half: a negotiate carrying no key is refused.

## Editing it

Two mistakes cost this script three runs the first time. Both are worth not repeating:

1. **Read the contract, do not guess it.** A submission requires an `Idempotency-Key` header (400
   `idempotency_key_required` without it) and answers 202, not 200. The read-back route returns
   `state`, not `status`, and the assessment id field is `assessmentId`. Every one of those was a
   confident guess that produced a red line about working code.
2. **Keep the run data out of the script's own directory.** The first version wrote its run root into
   the folder holding the script and cleared that folder at the start of `main()`, which deleted the
   script. `$PROBE_ROOT` is its own path now, and the clearing went with it: a directory per run
   gives the fresh database a run needs without a delete anywhere near the repository.
