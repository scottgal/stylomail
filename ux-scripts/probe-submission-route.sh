#!/usr/bin/env bash
# Measures what POST /v1/submissions answers, and what the console would then show.
#
# Why this exists: console-smoke.yaml's "What this harness cannot reach" has said
# since it was written that the queue stays empty because the harness Host's
# provider is unreachable and "POST /v1/submissions declines an assessment when
# the semantic provider is unavailable". That is a claim about a route, inherited
# from reading it. spec.md 14.5 rests a whole acceptance criterion on it: if the
# route declines rather than accepting-and-holding, then an empty queue is correct
# behaviour for a Host that cannot assess, and the corpus needs a Host with a
# working provider rather than more traffic. So it is measured here rather than
# assumed, on each Host shape the harness can start.
#
# Usage. The shape is chosen by the harness's own switches, and the run prints
# which one it measured:
#
#   ./ux-scripts/probe-submission-route.sh                          # jev, endpoint unreachable
#   CONSOLE_ASSESSOR=false ./ux-scripts/probe-submission-route.sh   # no assessor at all
#   CONSOLE_PROVIDER=nimble ./ux-scripts/probe-submission-route.sh  # a real local assessor
#
# What it prints, for each: readiness, the submission's HTTP status and body, the
# message listing and the decision ledger, and the quarantine release route when
# the message was quarantined. Everything read is written under $CONSOLE_RUN as
# well, so a claim can be re-read from the artifacts afterwards.
#
# It asserts nothing. It is an instrument: a probe that failed would have to
# decide what the right answer was before measuring it, which is the mistake this
# script exists to avoid.
#
# The key is read from the harness's generated principal file into a variable,
# written once to a 0600 header file and handed to curl as -H @file, so it is
# not in any argument list. It is never printed, and neither is any response
# header. The file is removed by console_stop_host on the way out.

set -uo pipefail

# Before the source, because console-harness.sh assigns this itself and a
# `${CONSOLE_RUN:-...}` after it keeps the harness's value. See ux-scripts/README.md,
# "Writing a script", step 3.
CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-probe-ux}"

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/console-harness.sh"

trap 'console_stop_host' EXIT INT TERM

# The scratch is wiped first, and this is not tidiness.
#
# Found 2026-09-30 by reading a probe run's own output: the Host's database lives
# under $CONSOLE_RUN/data, and without this the run reused the database a previous
# run left behind. The symptom is the worst possible one for an instrument. A
# default-shape (Jev, unreachable provider) run listed a held message and a
# 0.575 Hold decision scored by nimble:latest, neither of which that shape can
# produce, because both were still in the database from a Nimble-shape run
# sixteen minutes earlier. Read without noticing, that is a measurement saying
# "a Host that cannot assess nonetheless populated the queue", which is the exact
# claim this probe exists to settle.
#
# So the rule: a reading is only a reading if the Host that produced it started
# empty. This wipes before the keys are generated and before the Host is started,
# which is also why the artifacts above can still be re-read afterwards: they are
# written by this run, after the wipe.
#
# A consequence worth knowing: two probes running at once must not share this
# directory, since each would delete the other's Host state. Run shapes in
# sequence, or give a concurrent one its own CONSOLE_RUN.
rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

# One message, the same shape console_seed_decision uses: a display name that
# disagrees with its From address, an anchor whose text disagrees with its href,
# and a link host that is not the host it names. If a plant is ever added to the
# corpus, this is the same class of message and the same class of claim.
#
# CONSOLE_PROBE_MIME points it at a file to send instead, and
# CONSOLE_PROBE_AUTH=fail adds failing SPF and DKIM results to the request. Both
# exist because the question this probe gets asked most often is not "what does
# this one message do" but "what does it take for the queue to hold something":
# the outcome depends on the traffic, so measuring it means being able to vary
# the traffic without editing the instrument.
#
# What CONSOLE_PROBE_AUTH=fail does NOT do is make the message riskier, and the
# name invites the opposite belief. Measured 2026-09-30: the risk index is
# identical (35/73) whether the authentication results are absent, present and
# untrusted, or present and marked fromTrustedVerifier. What they change is the
# coverage of deterministic.trusted_authentication_failure, which reads
# NotApplicable, ReducedCoverage or Available respectively, and no risk
# dimension consumes that signal. So the switch is a way to vary the evidence,
# not a lever on the disposition, and a run that expects it to reach Held has
# the wrong model of this Host.
console_probe_message() {
    if [[ -n "${CONSOLE_PROBE_MIME:-}" ]]; then
        cp "$CONSOLE_PROBE_MIME" "$CONSOLE_RUN/probe.eml"
    else
        printf 'From: "Accounts" <security@exampple.test>\r\nTo: alice@example.test\r\nSubject: Urgent: verify your account\r\nDate: Tue, 22 Sep 2026 10:00:00 +0000\r\nMessage-ID: <probe-%s@exampple.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset="utf-8"\r\n\r\n<html><body><p>Verify your account within 24 hours.</p><p><a href="http://198.51.100.9/v">https://accounts.example.test/login</a></p></body></html>\r\n' "$$" > "$CONSOLE_RUN/probe.eml"
    fi

    local raw
    raw="$(base64 -i "$CONSOLE_RUN/probe.eml" | tr -d '\n')"

    PROBE_AUTH="${CONSOLE_PROBE_AUTH:-pass}" python3 - "$raw" > "$CONSOLE_RUN/probe.json" <<'PYEOF'
import json, os, sys

body = {
    "direction": "Inbound",
    "mailFrom": "security@exampple.test",
    "rcptTo": ["alice@example.test"],
    "rawMime": sys.argv[1],
    "connectingIp": "198.51.100.9",
}

if os.environ.get("PROBE_AUTH") == "fail":
    body["authenticationResults"] = [
        {"mechanism": "spf", "result": "Fail", "detail": "probe: sender is not permitted by the domain"},
        {"mechanism": "dkim", "result": "Fail", "detail": "probe: no valid signature"},
        {"mechanism": "dmarc", "result": "Fail", "detail": "probe: policy p=reject"},
    ]

print(json.dumps(body))
PYEOF
}

# The decision detail, not the listing row: the detail is where the evidence
# rows are, and whether the trend-window qualifier is present on a live response
# is a question the harness has never been able to answer.
console_probe_decision_detail() {
    local id="$1"
    [[ -z "$id" ]] && return 0
    console_probe_get "decision-$id" "/v1/decisions/$id"
}

# GET a route into a file and print the status and the file's contents. The body
# is whatever the Host said, including an error body: the error body is the
# measurement on the shapes where the route refuses.
console_probe_get() {
    local name="$1" path="$2"
    local code
    code="$(curl -sS -o "$CONSOLE_RUN/$name.json" -w '%{http_code}' \
        --max-time "${CONSOLE_PROBE_TIMEOUT:-60}" \
        -H @"$PROBE_AUTH_HEADERS" "$CONSOLE_BASE$path" 2>"$CONSOLE_RUN/$name.err")"

    echo "GET $path -> HTTP $code"
    cat "$CONSOLE_RUN/$name.json" 2>/dev/null
    echo
}

echo "=================================================================="
echo "shape: CONSOLE_ASSESSOR=${CONSOLE_ASSESSOR:-true} CONSOLE_PROVIDER=${CONSOLE_PROVIDER:-jev}"
echo "scratch: $CONSOLE_RUN"
echo "=================================================================="

console_build_all || exit 1
console_start_host || exit 1

# The run's key file has to exist before any shape is worth measuring, and the
# headers below are built from it. console_auth_headers reads the key itself and
# takes no key argument, deliberately: a parameter is a value the caller has to
# hold, and this probe should hold none.
if [[ ! -r "$CONSOLE_RUN/data/principal.key" ]]; then
    echo "No principal key at $CONSOLE_RUN/data/principal.key, so this Host was" >&2
    echo "not started by console_start_host and nothing here can authenticate." >&2
    exit 1
fi

# The key goes to disk once, at 0600 before it holds anything, and curl reads it
# back with -H @file: an argument list is readable by anything that can run ps,
# and no secret belongs in one. console_stop_host, which the trap above already
# calls on the way out, removes the file.
PROBE_AUTH_HEADERS="$(console_auth_headers)"

CONSOLE_PROBE_TIMEOUT="${CONSOLE_PROBE_TIMEOUT:-60}"
if [[ "${CONSOLE_PROVIDER:-jev}" == "nimble" && "${CONSOLE_ASSESSOR:-true}" == "true" ]]; then
    # A local model answers in seconds rather than milliseconds, and the
    # assessment asks it more than one question.
    CONSOLE_PROBE_TIMEOUT=300
fi

console_probe_get ready /health/ready

console_probe_message

# The idempotency key is required by the route, and per-run rather than fixed:
# a replay returns the earlier submission instead of assessing again, which would
# turn a second measurement into a read of the first one's answer.
# The POST's own response file is named for the POST. The read of the submission
# it returns is a different body from a different route, and naming both
# "submission" meant the later write silently replaced this one, which is how a
# measurement of the POST comes to be read off the GET.
echo "POST /v1/submissions --"
submission_code="$(curl -sS -o "$CONSOLE_RUN/submission-post.json" -w '%{http_code}' \
    --max-time "$CONSOLE_PROBE_TIMEOUT" \
    -H @"$PROBE_AUTH_HEADERS" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: probe-$$" \
    --data @"$CONSOLE_RUN/probe.json" "$CONSOLE_BASE/v1/submissions" 2>"$CONSOLE_RUN/submission-post.err")"
echo "POST /v1/submissions -> HTTP $submission_code"
cat "$CONSOLE_RUN/submission-post.json" 2>/dev/null
echo

console_probe_get messages /v1/messages

# The other two dispositions the route enumerates. Listed explicitly because the
# default is awaiting_decision, and a message that policy allowed is in normal
# delivery, which the route deliberately does not enumerate at all: an empty
# listing is not the same statement on these three as it is on the default.
console_probe_get messages-held "/v1/messages?state=held"
console_probe_get messages-quarantined "/v1/messages?state=quarantined"

console_probe_get decisions /v1/decisions

# The first decision's detail, which is where evidence rows live.
decision_id="$(python3 - "$CONSOLE_RUN/decisions.json" <<'PYEOF'
import json, sys
try:
    rows = json.load(open(sys.argv[1])).get("decisions") or []
except Exception:
    rows = []
print(rows[0].get("assessmentId", "") if rows else "")
PYEOF
)"
console_probe_decision_detail "$decision_id"

# The queue id exists if and only if delivery responsibility transferred, so this
# is the read that says whether anything is held. Read with python rather than
# sed: the response is one line and a greedy match would take the last id on it,
# which is the assessment's, not the queue's.
submission_id="$(python3 - "$CONSOLE_RUN/submission-post.json" <<'PYEOF'
import json, sys
try:
    body = json.load(open(sys.argv[1]))
except Exception:
    raise SystemExit(0)
print(body.get("queueId") or body.get("submissionId") or "")
PYEOF
)"
if [[ -n "$submission_id" ]]; then
    console_probe_get submission-get "/v1/submissions/$submission_id"
fi

# The state the console's release surface needs. Reached only if something is
# quarantined, which is the question this probe is asked most often: whether a
# working local provider gets a message far enough to be released.
quarantine_id="$(python3 - "$CONSOLE_RUN/messages-quarantined.json" <<'PYEOF'
import json, sys

try:
    body = json.load(open(sys.argv[1]))
except Exception:
    raise SystemExit(0)

rows = body.get("messages") or body.get("items") or []


def identity(row):
    for key in ("queueId", "submissionId", "id", "internalMessageId"):
        if row.get(key):
            return row[key]
    return ""


print(identity(rows[0]) if rows else "")
PYEOF
)"

if [[ -n "$quarantine_id" ]]; then
    echo "POST /v1/quarantine/{id}/release --"
    release_code="$(curl -sS -o "$CONSOLE_RUN/release.json" -w '%{http_code}' \
        --max-time "$CONSOLE_PROBE_TIMEOUT" \
        -H @"$PROBE_AUTH_HEADERS" \
        -H "Content-Type: application/json" \
        --data '{"reason":"probe"}' "$CONSOLE_BASE/v1/quarantine/$quarantine_id/release" \
        2>"$CONSOLE_RUN/release.err")"
    echo "POST /v1/quarantine/$quarantine_id/release -> HTTP $release_code"
    cat "$CONSOLE_RUN/release.json" 2>/dev/null
    echo
else
    echo "no quarantined row in the message listing, so the release route has nothing to act on"
fi

echo "artifacts: $CONSOLE_RUN/{ready,submission-post,submission-get,decision-*,messages,messages-held,messages-quarantined,decisions}.json"
