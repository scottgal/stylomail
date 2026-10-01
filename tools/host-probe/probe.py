#!/usr/bin/env python3
"""
Live acceptance probe: the Host's management surface against a real local decision model.

The suite points its provider at a refusing loopback port on purpose, so that a green run means the
same thing on every machine. This is the other half, and it cannot live in the suite: one live run
against the Nimble model actually serving on 11435, which is the only way to see a real semantic
value reach a real decision over real HTTP against a real database.

Three phases, because the surface has three ways in:

  1. POST /v1/assessments   a decision produced, persisted, and readable back.
  2. POST /v1/submissions   the same provider behind the queue, the worker and the policy.
  3. GET  /v1/traffic       a subscribed console, receiving the notice each decision produces.

The third subscribes before the first two run, so a notice asserted afterwards is one this probe was
already listening for rather than one it hopes was replayed, and it asserts the notice's subject is
the id of the decision this run just caused.

Run it with `python3 tools/host-probe/probe.py` from the repository root, after
`dotnet build src/StyloMail.Host/StyloMail.Host.csproj`. See README.md beside this file for what it
covers, what it does not, and the last measured result.

Secrets: the profile master key and the probe principal's key are generated here, written to 0600
files under a private run root, and read from those files by the child process. Neither value is
printed, echoed or passed on a command line, both files are removed at the end, and the child
environment is scrubbed of the two known secret variables before it is started, so this probe does
not inherit a key from whoever's shell runs it.

The run's own data lives under PROBE_ROOT, outside this directory: an earlier version kept its data
in its own folder and deleted itself at the start of a run. The default PROBE_ROOT is
`.styloagent/scratch/ingress/host-probe/` (gitignored), because the log behind a reported number has
to outlive the run that produced it and be readable by anyone who wants to re-check it. Each run
writes into its own `run-<UTC stamp>/`, so nothing is ever overwritten and this script deletes
nothing: the fresh database a run needs comes from a fresh directory, not from clearing an old one.
"""

from __future__ import annotations

from datetime import datetime, timezone

import atexit
import base64
import hashlib
import json
import os
import pathlib
import re
import secrets
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import websocket

REPO = pathlib.Path(__file__).resolve().parents[2]
PROBE_ROOT = pathlib.Path(os.environ.get(
    "PROBE_ROOT", str(REPO / ".styloagent/scratch/ingress/host-probe")))
ROOT = PROBE_ROOT / f"run-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}"
PORT = int(os.environ.get("PROBE_PORT", "5399"))
BASE = f"http://127.0.0.1:{PORT}"
OLLAMA = os.environ.get("PROBE_OLLAMA", "http://127.0.0.1:11435")
MODEL = os.environ.get("PROBE_MODEL", "nimble:latest")
HOST_DLL = REPO / "src/StyloMail.Host/bin/Debug/net10.0/StyloMail.Host.dll"

# The live-traffic hub, and the header every request to it carries. The key is presented on the
# negotiate request and again on the WebSocket handshake, and never in the query string: SignalR's
# usual access-token pattern puts it in the URL, where it lands in access logs, proxies and crash
# reports. The host has no query-string token path at all, so the only way to authenticate a
# subscription is the way this probe does it.
HUB_PATH = "/v1/traffic"
API_KEY_HEADER = "X-StyloMail-Key"
NOTICE_METHOD = "traffic"

TENANT = "acme"
SENDER = "billing@accounts-example.com"
RECIPIENT = "recipient@example.com"

# Deliberately an obvious credential phish: it gives the semantic layer something to have an opinion
# about, and it is the shape the operator's graph is meant to make visible over a thread. It reaches
# `Hold` rather than `Quarantine`: quarantine needs a message whose firing dimensions carry enough
# weight, which a single fixture does not.
MIME = "\r\n".join([
    'From: "Billing" <billing@accounts-example.com>',
    f"To: <{RECIPIENT}>",
    "Subject: URGENT: your mailbox will be closed today",
    "Message-ID: <probe-1@accounts-example.com>",
    "Date: Mon, 21 Sep 2026 09:00:00 +0000",
    "Content-Type: text/plain; charset=utf-8",
    "",
    "Your account will be deactivated within 24 hours. Confirm your password and card number at",
    "http://accounts-example.com/verify immediately or lose access.",
    "",
])

checks: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    checks.append((name, ok, detail))
    print(f"CHECK {name}: {'PASS' if ok else 'FAIL'}{'  ' + detail if detail else ''}", flush=True)
    return ok


def post(path: str, body: dict, key: str, timeout: float = 180.0,
         extra_headers: dict[str, str] | None = None) -> tuple[int, dict | str]:
    data = json.dumps(body).encode()
    headers = {"Content-Type": "application/json", "X-StyloMail-Key": key}
    headers.update(extra_headers or {})
    request = urllib.request.Request(BASE + path, data=data, method="POST", headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, json.loads(response.read() or b"{}")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")


def get(path: str, key: str | None = None, timeout: float = 30.0) -> tuple[int, dict | str]:
    request = urllib.request.Request(BASE + path)
    if key:
        request.add_header("X-StyloMail-Key", key)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read() or b"{}"
            try:
                return response.status, json.loads(raw)
            except json.JSONDecodeError:
                return response.status, raw.decode(errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors="replace")


def decisions(key: str) -> list[dict]:
    """This tenant's newest decisions, newest first, or an empty list."""
    status, listing = get("/v1/decisions?limit=20", key)
    if status != 200 or not isinstance(listing, dict):
        return []
    rows = listing.get("decisions") or listing.get("items") or []
    return [row for row in rows if isinstance(row, dict)]


class TrafficFeed:
    """
    A subscribed console: negotiate with the key, connect with the key, read notices.

    <para>
    This speaks the SignalR JSON protocol by hand over a plain WebSocket, deliberately: the point of
    this proof is the wire itself, and the honest way to read the wire is not to borrow the same
    client library the console uses. Three things are then visible in one place. The credential
    travels in a header on the negotiate request and again on the WebSocket handshake, never in the
    URL. The hub acknowledges the protocol handshake before it sends anything, so a subscription
    that was never established cannot be mistaken for a quiet one. And every notice arrives as an
    invocation of the one method the hub declares, carrying which thing changed and when.
    </para>
    """

    def __init__(self, key: str) -> None:
        self._key = key
        self._socket = None

    def open(self) -> None:
        status, body = post(f"{HUB_PATH}/negotiate?negotiateVersion=1", {}, self._key, timeout=15.0)
        if status != 200 or not isinstance(body, dict) or not body.get("connectionToken"):
            raise RuntimeError(f"negotiate answered HTTP {status}: {body}")

        token = body["connectionToken"]
        url = f"ws://127.0.0.1:{PORT}{HUB_PATH}?id={urllib.parse.quote(token)}"
        self._socket = websocket.create_connection(
            url, header=[f"{API_KEY_HEADER}: {self._key}"], timeout=15)

        # The protocol handshake, terminated by the record separator SignalR frames messages with.
        # Read rather than written and forgotten: a refused subscription answers here, and only here.
        self._socket.send('{"protocol":"json","version":1}\x1e')
        acknowledgement = self._socket.recv()
        if acknowledgement.strip() != "{}":
            raise RuntimeError(f"the hub refused the protocol handshake: {acknowledgement[:200]}")

    def notices(self, seconds: float, until=None) -> list[dict]:
        """
        Every notice that arrives within the window, plus anything already buffered.

        `until` stops the read early once the collected notices satisfy it, so waiting for one
        specific notice does not cost the whole window every time.
        """
        found: list[dict] = []
        deadline = time.time() + seconds
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                break
            self._socket.settimeout(remaining)
            try:
                frame = self._socket.recv()
            except (websocket.WebSocketTimeoutException, websocket.WebSocketConnectionClosedException):
                break
            if isinstance(frame, bytes):
                frame = frame.decode(errors="replace")

            # One frame can carry more than one message, and messages are separated rather than
            # framed individually, so the split is the protocol's and not a convenience.
            for piece in frame.split("\x1e"):
                if not piece.strip():
                    continue
                message = json.loads(piece)
                if message.get("type") == 1 and message.get("target") == NOTICE_METHOD:
                    found.extend(a for a in message.get("arguments") or [] if isinstance(a, dict))
                elif message.get("type") != 6:
                    # 6 is the keepalive ping. Anything else is printed rather than dropped: a
                    # completion, a close or an error is a fact about the subscription.
                    print(f"  OBSERVE hub frame: {piece[:200]}", flush=True)

            if until is not None and until(found):
                break

        return found

    def close(self) -> None:
        if self._socket is not None:
            try:
                self._socket.close()
            except Exception:
                pass
            self._socket = None


# Anchored on the hub's own path, so a query parameter named `id` on any other route is left alone:
# only the hub's `?id=` carries a value that authenticates a connection.
CONNECTION_TOKEN = re.compile(rf"({re.escape(HUB_PATH)}\?id=)[A-Za-z0-9_.\-]+")


def redact_connection_token(path: pathlib.Path) -> int:
    """Replace the SignalR connection token in a host log with a marker, and say how many it found.

    The token rides in the query string of the WebSocket request, so the host's own request log
    contains it. It is per-connection and dead once the host has stopped, but this log outlives the
    run and gets read, copied and quoted, so it does not travel.
    """
    if not path.exists():
        return 0
    text = path.read_text(errors="replace")
    scrubbed, count = CONNECTION_TOKEN.subn(r"\1<redacted>", text)
    if count:
        path.write_text(scrubbed)
    return count


# Registered as well as called, because the call at the end of a run is on the happy path only. A
# probe that raises before it gets there leaves the token in a log that outlives the socket, which is
# exactly the case the redaction exists for; this is idempotent and tolerates an absent file, so the
# second call is a no-op and the first survives a crash.
atexit.register(redact_connection_token, ROOT / "host.log")


def stamp_build() -> str:
    """The identity of the binary this run executes, written beside its log.

    A run identifier says WHEN a number was taken, not WHAT took it. This probe starts the Host from
    the prebuilt assembly, so every number it reports is a number about whatever tree that assembly
    was last built from: an engine change that is uncommitted when the build happens is inside the
    binary and leaves no mark on the run's output, and no reading of load or swap can tell a reader
    which engine ran. The assembly's digest and build time, with HEAD and a digest of the uncommitted
    paths at launch, are what let a reader name the build behind the counts.

    Written before the child starts, so the stamp describes the binary that ran rather than one that
    was in place afterwards.
    """
    stat = HOST_DLL.stat()
    built = datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat(timespec="seconds")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=str(REPO),
                          capture_output=True, text=True).stdout.strip()
    status = subprocess.run(["git", "status", "--porcelain"], cwd=str(REPO),
                            capture_output=True, text=True).stdout
    uncommitted = [line for line in status.splitlines() if line.strip()]
    return "\n".join([
        f"host assembly: {HOST_DLL}",
        f"host assembly sha256: {hashlib.sha256(HOST_DLL.read_bytes()).hexdigest()}",
        f"host assembly built (UTC, from mtime): {built}",
        f"HEAD at launch: {head}",
        f"uncommitted paths at launch: {len(uncommitted)}",
        f"uncommitted status sha256: {hashlib.sha256(status.encode()).hexdigest()}",
    ])


def main() -> int:
    if not HOST_DLL.exists():
        print(f"FAIL: {HOST_DLL} is not built. Run: dotnet build src/StyloMail.Host/StyloMail.Host.csproj",
              flush=True)
        return 1

    # The run's database and log go in their own directory, so a run never has to clear anything to
    # start clean and the previous run's evidence is still there afterwards. If the name is taken,
    # two runs started inside the same second; say so rather than delete one of them.
    try:
        ROOT.mkdir(parents=True, mode=0o700)
    except FileExistsError:
        print(f"FAIL: {ROOT} already exists. Two runs began in the same second; retry, or set "
              f"PROBE_ROOT to a directory of your own.", flush=True)
        return 1
    print(f"RUN ROOT: {ROOT}", flush=True)

    # Every number below is a number about this assembly. Written and printed here, before the child
    # is started, so the artifact carries what took the measurement and not only when.
    stamp = stamp_build()
    (ROOT / "build.txt").write_text(stamp + "\n")
    print(stamp, flush=True)

    profile_key_file = ROOT / "profile.key"
    principal_key_file = ROOT / "principal.key"
    for path, value in ((profile_key_file, secrets.token_hex(24)),
                        (principal_key_file, "probe-" + secrets.token_hex(16))):
        path.write_text(value)
        path.chmod(0o600)

    # Read the two values once, here, and never again: they reach the child through its environment
    # and are not written, echoed or formatted anywhere else in this script.
    profile_key = profile_key_file.read_text()
    principal_key = principal_key_file.read_text()

    environment = {k: v for k, v in os.environ.items()
                   if k not in ("TYPESAFE_API_KEY", "STYLOMAIL_PROFILE_KEY", "STYLOMAIL_PROFILE_KEY_FILE")}
    environment.update({
        "ASPNETCORE_URLS": BASE,
        "STYLOMAIL_PROFILE_KEY": profile_key,
        "StyloMail__Assessment__Provider": "Nimble",
        # The route the adapter actually posts to, and not a preference. `/2` made the decision route
        # `POST /v1/systemone`, and this line used to name the superseded `/api/generate`. A body shaped
        # `{model, state, questions}` sent to the generate route is not the experiment this probe claims
        # to run: it would degrade to an Unavailable assessment and read as a host defect.
        #
        # What the other end serves was READ rather than assumed. At `127.0.0.1:11435`, `GET /api/version`
        # returns 200 and `POST /v1/systemone` returns 200, while `POST /api/systemone` returns 404
        # (`.styloagent/scratch/overview/probe-systemone.out`, 1 Oct). The shipping default in
        # `NimbleOptions.cs:56` ends in `/v1/systemone` as well, so this override names the same route the
        # adapter would use unaided rather than a second one it would not.
        "StyloMail__Nimble__Endpoint": f"{OLLAMA}/v1/systemone",
        "StyloMail__Nimble__Model": MODEL,
        "StyloMail__Storage__SpoolRoot": str(ROOT / "spool"),
        "StyloMail__Storage__DatabasePath": str(ROOT / "host.db"),
        # The live feed is off by default and a deployment without it is complete, so it is enabled
        # here rather than assumed: a hub that is not mapped answers 404 on the negotiate route,
        # which is a different fact from a subscription that was refused.
        "StyloMail__Traffic__Enabled": "true",
        # Phase 2 only needs the worker to be running: whether a delivery ever lands is not what this
        # probe is about, and TEST-NET-1 is never routable, so nothing leaves this machine.
        "StyloMail__Transport__Upstream__Host": "192.0.2.1",
        "StyloMail__Transport__Upstream__Port": "25",
        "StyloMail__Transport__Upstream__Tls": "None",
        "StyloMail__Auth__Principals__0__PrincipalId": "probe",
        "StyloMail__Auth__Principals__0__Key": principal_key,
        "StyloMail__Auth__Principals__0__TenantId": TENANT,
        "StyloMail__Auth__Principals__0__Privileges__0": "Assess",
        "StyloMail__Auth__Principals__0__Privileges__1": "Send",
        "StyloMail__Auth__Principals__0__Privileges__2": "Review",
        "StyloMail__Auth__Principals__0__Privileges__3": "Administer",
        "StyloMail__Auth__Principals__0__Privileges__4": "Feedback",
    })

    log = (ROOT / "host.log").open("w")
    child = subprocess.Popen(
        ["dotnet", str(HOST_DLL), "serve"],
        cwd=str(REPO),
        env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)

    feed = None

    try:
        ready = False
        for _ in range(60):
            if child.poll() is not None:
                break
            try:
                status, body = get("/health/ready", timeout=2.0)
                if status == 200:
                    ready = (body if isinstance(body, dict) else {}).get("status") == "ready"
                    break
            except Exception:
                pass
            time.sleep(0.5)
        if not check("host_started_ready", ready, "GET /health/ready returned status ready"):
            print(f"  child exit code: {child.poll()}  (see {ROOT / 'host.log'})", flush=True)
            return 1

        status, _ = get("/health/live")
        check("liveness", status == 200, f"HTTP {status}")

        # Subscribe before anything is submitted, so the notices this probe asserts on are ones it
        # was already listening for rather than ones it hopes were replayed.
        #
        # The refusal first: a subscription route that answers without a credential is a feed open
        # to anyone who can reach the port, and that is worth measuring before measuring the feed.
        status, _ = post(f"{HUB_PATH}/negotiate?negotiateVersion=1", {}, "", timeout=10.0)
        check("hub_refuses_an_unkeyed_negotiate", status in (401, 403), f"HTTP {status}")

        try:
            feed = TrafficFeed(principal_key)
            feed.open()
            check("hub_subscription_authenticated", True,
                  f"negotiated and connected to {HUB_PATH} with the key in the header on both")
        except Exception as error:
            feed = None
            check("hub_subscription_authenticated", False, f"{type(error).__name__}: {error}")

        message = {
            "tenantId": TENANT,
            "direction": "Outbound",
            "mailFrom": SENDER,
            "rcptTo": [RECIPIENT],
            "rawMime": base64.b64encode(MIME.encode()).decode(),
            "authenticatedAccount": SENDER,
            "approvedSenderIdentities": [SENDER],
        }

        # ---------------------------------------------------------------- phase 1: assess
        started = time.time()
        status, decision = post("/v1/assessments", message, principal_key)
        elapsed = time.time() - started
        if not check("assess_http_200", status == 200, f"HTTP {status} in {elapsed:.1f}s"):
            print(f"  body: {decision}", flush=True)
            return 1
        assert isinstance(decision, dict)

        action = decision.get("action")
        decision_id = decision.get("assessmentId") or decision.get("decisionId") or decision.get("id")
        check("assess_action", bool(action), f"action={action}")
        if not decision_id:
            print(f"  OBSERVE assessment response keys: {sorted(decision)}", flush=True)
        else:
            print(f"  OBSERVE assessment id field: {decision_id}", flush=True)

        evidence = decision.get("evidence") or []
        semantic = [row for row in evidence if row.get("origin") == "Semantic"]
        scored = [row for row in semantic if row.get("availability") == "Available" and row.get("value") is not None]
        unanswered = [row for row in semantic if row.get("availability") != "Available"]

        check("semantic_rows_present", bool(semantic), f"{len(semantic)} semantic rows")
        check("semantic_value_from_the_live_model", bool(scored),
              f"{len(scored)} scored, {len(unanswered)} unanswered")
        for row in semantic:
            print(f"    {row.get('signalId')}: {row.get('availability')} value={row.get('value')}", flush=True)

        if decision_id:
            status, recorded = get(f"/v1/decisions/{decision_id}", principal_key)
            ok = status == 200 and isinstance(recorded, dict)
            same = ok and recorded.get("action") == action
            check("decision_persisted_and_matches", same,
                  f"HTTP {status} action={recorded.get('action') if ok else 'n/a'}")

        # The ledger write is what publishes, so a notice for THIS assessment is the whole chain in
        # one fact: the decision was recorded, the ledger announced it, the hub addressed it to this
        # tenant's group, and this subscription received it. A notice from another message would not
        # do, which is why the assertion is on the id rather than on having heard something.
        if feed is not None and decision_id:
            notices = feed.notices(20, until=lambda found: any(
                n.get("kind") == "DecisionRecorded" and n.get("subjectId") == decision_id
                for n in found))
            print(f"OBSERVE hub notice kinds after the assessment: "
                  f"{sorted({n.get('kind') for n in notices})}", flush=True)
            matched = [n for n in notices
                       if n.get("kind") == "DecisionRecorded" and n.get("subjectId") == decision_id]
            check("hub_announced_the_recorded_decision", bool(matched),
                  f"DecisionRecorded subjectId={decision_id}" if matched
                  else f"no notice for {decision_id} within 20s")
            if notices:
                print(f"OBSERVE first notice: {json.dumps(notices[0])}", flush=True)

        # ------------------------------------------------------- phase 2: submit through the queue
        before = len(decisions(principal_key))

        submission = dict(message)
        submission["rawMime"] = base64.b64encode((MIME + "Probe phase two.\r\n").encode()).decode()
        status, accepted = post("/v1/submissions", submission, principal_key,
                                extra_headers={"Idempotency-Key": "probe-" + secrets.token_hex(8)})
        detail = sorted(accepted) if isinstance(accepted, dict) else accepted
        # 202 and not 200: a submission is accepted for work that happens after the response, which
        # is the honest status for a route whose caller cannot wait for the assessment. The first
        # version of this probe asserted 200 and reported a working route as a failure.
        check("submit_accepted", status in (200, 202), f"HTTP {status} keys={detail}")

        queue_id = accepted.get("queueId") if isinstance(accepted, dict) else None
        if queue_id:
            status, state = get(f"/v1/submissions/{queue_id}", principal_key)
            reported = state.get("state") if isinstance(state, dict) else state
            print(f"OBSERVE submission_read_back HTTP {status} state={reported}", flush=True)
            check("submission_read_back", status == 200, f"HTTP {status}")

        observed = None
        for _ in range(40):
            time.sleep(1.5)
            rows = decisions(principal_key)
            if len(rows) > before:
                observed = rows[0]
                break
        # A NEW decision, not any decision: an earlier version counted the assessment above as the
        # queue's work and passed whether or not the submission did anything.
        check("queue_reached_a_new_decision", observed is not None,
              f"action={observed.get('action') if observed else 'none within 60s'}")

        # The same announcement, reached the other way in: this decision was produced by the queue's
        # worker rather than by a request, and the submission response is where its id came from.
        queued_decision = accepted.get("assessmentId") if isinstance(accepted, dict) else None
        if feed is not None and queued_decision:
            notices = feed.notices(20, until=lambda found: any(
                n.get("kind") == "DecisionRecorded" and n.get("subjectId") == queued_decision
                for n in found))
            print(f"OBSERVE hub notice kinds after the submission: "
                  f"{sorted({n.get('kind') for n in notices})}", flush=True)
            matched = [n for n in notices
                       if n.get("kind") == "DecisionRecorded" and n.get("subjectId") == queued_decision]
            check("hub_announced_the_queues_decision", bool(matched),
                  f"DecisionRecorded subjectId={queued_decision}" if matched
                  else f"no notice for {queued_decision} within 20s")

        status, messages = get("/v1/messages", principal_key)
        rows = (messages.get("messages") or messages.get("items") or []) if isinstance(messages, dict) else []
        print(f"OBSERVE messages_listing_rows={len(rows)} HTTP {status}", flush=True)
        if isinstance(accepted, dict):
            print(f"OBSERVE submission response keys: {sorted(accepted)}", flush=True)
    finally:
        if feed is not None:
            feed.close()

        # How the host died is reported rather than assumed, because a graceful stop is part of what
        # this probe is for: the delivery worker's bounded drain runs on the way out and nothing else
        # here exercises it. The wait is deliberately longer than the drain window (30s by default),
        # and a host that has to be SIGKILLed says so: an earlier version waited 20s, shorter than the
        # drain, so it killed the host without ever mentioning it.
        os.killpg(os.getpgid(child.pid), signal.SIGTERM)
        try:
            child.wait(timeout=45)
            print(f"SHUTDOWN exit={child.returncode} after SIGTERM", flush=True)
        except subprocess.TimeoutExpired:
            os.killpg(os.getpgid(child.pid), signal.SIGKILL)
            child.wait(timeout=10)
            print(f"SHUTDOWN SIGKILLed after 45s (the drain did not finish); "
                  f"exit={child.returncode}", flush=True)
        log.close()
        # Before anything can read or copy it: the host logs the hub request, and the hub's
        # connection token is in that request's query string. The run's log stays on disk, so the
        # token is taken out of it here rather than left in an artifact that outlives the socket.
        redacted = redact_connection_token(ROOT / "host.log")
        print(f"NOTE redacted {redacted} connection token(s) in {ROOT / 'host.log'}", flush=True)
        # The two key files go with the run; the log and the database stay for inspection.
        for path in (profile_key_file, principal_key_file):
            path.unlink(missing_ok=True)

    failed = [name for name, ok, _ in checks if not ok]
    print(f"\nSUMMARY {len(checks) - len(failed)}/{len(checks)} passed"
          + (f"; failed: {', '.join(failed)}" if failed else ""), flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
