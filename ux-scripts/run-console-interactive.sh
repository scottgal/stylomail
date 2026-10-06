#!/usr/bin/env bash
# Opens the real console against a Host this script owns, then stops that Host
# when the window, UX REPL, or UX MCP session exits. This is the manual
# counterpart to the scripted runners: no operator has to assemble a Host URL
# and a key, which would make it too easy to explore a different backend.
#
# Default mode seeds a real decision through the Host's assessment route. Its
# provider is deliberately unreachable, so it needs no model or provider key;
# the resulting decision is still fetched and rendered over the console's HTTP
# client. Set CONSOLE_INTERACTIVE_CONTENT=fixture for a render-only pane using
# decision-fixture.json instead. That mode is labelled here because it proves
# the view, not policy behaviour or the message-to-decision join.
#
# CONSOLE_INTERACTIVE_DRIVER selects how to explore the same controlled system:
#   window (default) - ordinary Avalonia window
#   repl             - Mostlylucid.Avalonia.UITesting interactive REPL
#   mcp              - Mostlylucid.Avalonia.UITesting JSON-RPC MCP server
#   headless         - runs the normal console smoke through this entrypoint
#   trend-fixture    - screenshots labelled trend rows and probes Slack history
#
# Set CONSOLE_INTERACTIVE_CORPUS=true in Host mode to generate and seed the
# corpus-owned mailbox population. It reports only observations from
# ui-scenarios.json, never a model-predicted queue outcome.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONSOLE_RUN_DEFAULT="${CONSOLE_INTERACTIVE_RUN_DIR:-/tmp/stylomail-console-interactive-ux}"

case "$CONSOLE_RUN_DEFAULT" in
    /tmp/stylomail-console-interactive-*) ;;
    *)
        echo "CONSOLE_INTERACTIVE_RUN_DIR must remain below /tmp/stylomail-console-interactive-." >&2
        exit 2
        ;;
esac

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    local exit_status=$?
    if [[ -n "${CONSOLE_RUN:-}" ]]; then
        console_stop_host
    fi
    trap - EXIT
    exit "$exit_status"
}
on_interrupt() {
    echo "interactive console interrupted; stopping its Host" >&2
    exit 130
}
trap cleanup EXIT
trap on_interrupt INT TERM

content="${CONSOLE_INTERACTIVE_CONTENT:-host}"
driver="${CONSOLE_INTERACTIVE_DRIVER:-window}"

case "$content" in
    host|fixture) ;;
    *)
        echo "CONSOLE_INTERACTIVE_CONTENT must be 'host' or 'fixture', not '$content'." >&2
        exit 2
        ;;
esac

case "$driver" in
    window) app_args=() ;;
    repl) app_args=(--ux-repl) ;;
    mcp) app_args=(--ux-mcp) ;;
    headless)
        app_args=(
            --ux-headless
            --ux-test
            --script ux-scripts/console-smoke.yaml
        )
        ;;
    trend-fixture)
        app_args=(
            --ux-headless
            --ux-test
        )
        content=fixture
        ;;
    *)
        echo "CONSOLE_INTERACTIVE_DRIVER must be 'window', 'repl', 'mcp', 'headless', or 'trend-fixture', not '$driver'." >&2
        exit 2
        ;;
esac

if console_port_is_taken; then
    echo "Port $CONSOLE_PORT is already in use, so the interactive launcher will not" >&2
    echo "clear $CONSOLE_RUN_DEFAULT before refusing a second session's backend." >&2
    exit 1
else
    port_probe=$?
fi
if [[ "$port_probe" -ne 1 ]]; then
    echo "The port probe could not determine whether $CONSOLE_PORT is free; refusing" >&2
    echo "before creating or clearing this session's run directory." >&2
    exit 1
fi

# This must precede the build because the build fingerprint belongs to this
# run, and an inherited run directory is another run's credentials and evidence.
console_runner_run_dir "$CONSOLE_RUN_DEFAULT" || exit 2
if [[ "$driver" == "headless" || "$driver" == "trend-fixture" ]]; then
    # Keep every invocation's screenshot and result beside its unique run state.
    # A fixed ux-results directory made a later manual review erase the earlier one.
    CONSOLE_RESULTS="$CONSOLE_RUN/ui-results"
fi
if [[ "$driver" == "trend-fixture" ]]; then
    # CONSOLE_RUN is canonicalized by console_runner_run_dir above. Build the
    # per-run scenario argument only after that assignment, like the output path.
    app_args+=(--script "$CONSOLE_RUN/data/console-trend-review-smoke.yaml" --output "$CONSOLE_RESULTS")
elif [[ "$driver" == "headless" ]]; then
    app_args+=(--output "$CONSOLE_RESULTS")
fi
console_build_all || exit 1

if [[ "${DESKTOP_DIAGNOSTIC_PROCESS_CENSUS:-false}" == "true" ]]; then
    python3 - "$$" <<'PYEOF'
import subprocess
import sys

entry_pid = sys.argv[1]
try:
    group = subprocess.run(
        ["ps", "-p", entry_pid, "-o", "pgid="],
        check=True,
        capture_output=True,
        text=True,
        timeout=3,
    ).stdout.strip()
    listing = subprocess.run(
        ["ps", "-axo", "pid,ppid,pgid,stat,etime,comm="],
        check=True,
        capture_output=True,
        text=True,
        timeout=3,
    ).stdout
except Exception as error:
    print(f"== post-build process census unavailable ({type(error).__name__}); status unknown ==")
else:
    print(f"== post-build process census: entry PID {entry_pid}, PGID {group} ==")
    for line in listing.splitlines()[1:]:
        fields = line.strip().split(None, 5)
        if len(fields) == 6 and fields[2] == group:
            print(
                f"== post-build member PID={fields[0]} PPID={fields[1]} PGID={fields[2]} "
                f"STAT={fields[3]} ETIME={fields[4]} COMM={fields[5]} =="
            )
PYEOF
fi

if [[ "$driver" == "trend-fixture" ]]; then
    # The displayed trend rows remain fixture-only. Populate conversation
    # history from three signed events accepted by this throwaway local Host.
    export StyloMail__Slack__Enabled=true
    export StyloMail__Slack__SigningSecret="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
    export StyloMail__Slack__OwnBotId="B-desktop-not-self"
    export StyloMail__Slack__InboundTenantId="harness"
    export StyloMail__Slack__WatchedChannels__0="C01"
    export STYLOMAIL_SMOKE_HISTORY_WORKSPACE="T01"
    export STYLOMAIL_SMOKE_HISTORY_CHANNEL="C01"
    export STYLOMAIL_SMOKE_HISTORY_PAGE_SIZE=2
fi

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1
echo "== throwaway Host PID: $CONSOLE_HOST_PID =="

# The ordinary Host route is exercised in the default. The fixture switch is
# useful for inspecting layout with known data, but deliberately seeds nothing:
# a visible fixture must never be described as a decision the Host made.
if [[ "$content" == "host" ]]; then
    echo "== seeding a real Host decision (no model required) =="
    console_seed_management || exit 1
    console_seed_decision "$CONSOLE_BASE" || exit 1
    export CONSOLE_DECISION_FIXTURE=false
    echo "== content: Host-produced decision; UI and HTTP client are both in scope =="

    if [[ "${CONSOLE_INTERACTIVE_CORPUS:-false}" == "true" ]]; then
        CONSOLE_CORPUS_SEED="${CONSOLE_INTERACTIVE_CORPUS_SEED:-20261006}"
        CONSOLE_CORPUS_COUNT="${CONSOLE_INTERACTIVE_CORPUS_COUNT:-24}"
        CONSOLE_CORPUS_PROFILE="${CONSOLE_INTERACTIVE_CORPUS_PROFILE:-mailbox}"
        CONSOLE_CORPUS_BATCH="$CONSOLE_RUN/data/corpus"
        CONSOLE_CORPUS_TOOL="${CONSOLE_CORPUS_CLI:-$CONSOLE_REPO/tools/corpus/corpus.py}"

        echo "== generating corpus: seed=$CONSOLE_CORPUS_SEED count=$CONSOLE_CORPUS_COUNT profile=$CONSOLE_CORPUS_PROFILE =="
        python3 "$CONSOLE_CORPUS_TOOL" generate \
            --seed "$CONSOLE_CORPUS_SEED" \
            --count "$CONSOLE_CORPUS_COUNT" \
            --out "$CONSOLE_CORPUS_BATCH" \
            --profile "$CONSOLE_CORPUS_PROFILE" \
            --coverage full || exit 1

        export CONSOLE_CORPUS="$CONSOLE_CORPUS_BATCH"
        console_seed_corpus || exit 1

        python3 - "$CONSOLE_CORPUS_BATCH/ui-scenarios.json" <<'PYEOF'
import json, sys

path = sys.argv[1]
with open(path, encoding="utf-8") as source:
    contract = json.load(source)

version = contract.get("uiScenarioContractVersion")
manifest = contract.get("manifest", {})
selectors = contract.get("selectors", {})
print(f"== corpus UI contract: {path} (v{version}) ==")
print("corpus input: " + " ".join(
    f"{name}={manifest.get(name)!r}" for name in ("seed", "profile", "coverage")))
for name in ("anyQueuedOrHeld", "anyDecisionJoinResolved"):
    selector = selectors.get(name, {})
    ids = selector.get("scenarioIds", [])
    state = "satisfied" if selector.get("satisfied") else "not observed"
    print(f"{name}: {state}; scenarioIds={','.join(ids) if ids else '-'}")
verification = selectors.get("allPlantedFactsVerified", {})
print("allPlantedFactsVerified: " + verification.get("status", "notChecked"))
PYEOF
    fi
else
    export CONSOLE_DECISION_FIXTURE=true
    echo "== content: local fixture only; no corpus contract was seeded or verified =="
fi

console_export_app_env
if [[ "$driver" == "trend-fixture" ]]; then
    # Trend rows remain fixture content; conversation history below is real
    # local Host output produced through signed Slack ingress.
    mkdir -p "$CONSOLE_RUN/data" || exit 1
    console_auth_headers "$CONSOLE_RUN/auth.headers" >/dev/null || exit 1
    python3 - "$CONSOLE_BASE" "$CONSOLE_RUN/auth.headers" "$CONSOLE_RUN/data" \
        "$HERE/decision-fixture.json" "$HERE/console-trend-review-smoke.yaml" <<'PYEOF'
import hashlib
import hmac
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

base, auth_path, data_dir, fixture_path, scenario_path = sys.argv[1:]
workspace = os.environ["STYLOMAIL_SMOKE_HISTORY_WORKSPACE"]
channel = os.environ["STYLOMAIL_SMOKE_HISTORY_CHANNEL"]
signing_secret = os.environ["StyloMail__Slack__SigningSecret"].encode("utf-8")
headers = {}
with open(auth_path, encoding="utf-8") as source:
    for line in source:
        name, value = line.split(":", 1)
        headers[name.strip()] = value.strip()

now = time.time()
root = f"{now - 3:.6f}"
events = [
    ("Ev-desktop-1", f"{now - 2:.6f}", "Morning team, the deployment review is at ten."),
    ("Ev-desktop-2", f"{now - 1:.6f}", "I will bring the latency chart to the review."),
    ("Ev-desktop-3", f"{now:.6f}", "Please add the rollback checklist to our agenda."),
]

def request(path, body=None, extra_headers=None):
    request_headers = dict(headers)
    request_headers.update(extra_headers or {})
    if body is not None:
        request_headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=body, headers=request_headers)
    try:
        with urllib.request.urlopen(req, timeout=2) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        error.read()
        raise RuntimeError(f"Local harness request returned HTTP {error.code}")

for event_id, message_ts, message in events:
    payload = {
        "type": "event_callback", "event_id": event_id,
        "event_time": int(now), "team_id": workspace,
        "event": {"type": "message", "channel": channel,
                  "user": "U-desktop-review", "text": message,
                  "ts": message_ts, "thread_ts": root},
    }
    raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    timestamp = str(int(time.time()))
    signature_base = b"v0:" + timestamp.encode("ascii") + b":" + raw
    signature = "v0=" + hmac.new(signing_secret, signature_base, hashlib.sha256).hexdigest()
    status, _ = request("/v1/ingress/slack", raw, {
        "X-Slack-Request-Timestamp": timestamp,
        "X-Slack-Signature": signature,
    })
    if status != 200:
        raise RuntimeError(f"Slack ingress admission returned HTTP {status}")

filters = {"workspaceId": workspace, "channelId": channel,
           "threadId": root, "limit": "100"}
deadline = time.monotonic() + 30
records = []
all_records = []
while time.monotonic() < deadline:
    status, response_body = request("/v1/conversations/history?" + urllib.parse.urlencode(filters))
    if status != 200:
        raise RuntimeError(f"History read returned HTTP {status}")
    full_page = json.loads(response_body)
    records = full_page.get("decisions", [])
    if len(records) >= 3:
        all_records = records
        break
    time.sleep(0.2)
if len(records) != 3:
    raise RuntimeError(f"Expected three assessed conversation records, observed {len(records)}")

page_filters = dict(filters, limit="2")
status, response_body = request("/v1/conversations/history?" + urllib.parse.urlencode(page_filters))
first = json.loads(response_body)
first_records = first.get("decisions", [])
cursor = first.get("nextCursor")
if status != 200 or len(first_records) != 2 or first.get("hasMore") is not True or not cursor:
    raise RuntimeError("History page one did not contain two records and a cursor")
status, response_body = request("/v1/conversations/history?" + urllib.parse.urlencode(dict(page_filters, after=cursor)))
second = json.loads(response_body)
second_records = second.get("decisions", [])
if status != 200 or len(second_records) != 1 or second.get("hasMore") is not False:
    raise RuntimeError("Cursor page did not contain the remaining single record")
for page in (first, second):
    if (page.get("workspaceId"), page.get("channelId"), page.get("threadId")) != (workspace, channel, root):
        raise RuntimeError("History response did not echo the requested Slack conversation keys")
ids = [item.get("assessmentId", "") for item in first_records + second_records]
internal_ids = [item.get("internalMessageId", "") for item in first_records + second_records]
if len(set(ids)) != 3 or len(set(internal_ids)) != 3:
    raise RuntimeError("History records did not contain distinct assessment and message IDs")
if set(ids) != {item.get("assessmentId") for item in all_records}:
    raise RuntimeError("The cursor pages did not reproduce the complete history read")
if any(item.get("channel", {}).get("workspaceId") != workspace
       or item.get("channel", {}).get("channelId") != channel
       or item.get("channel", {}).get("threadId") != root
       for item in first_records + second_records):
    raise RuntimeError("A returned decision did not belong to the seeded Slack conversation")
if any(not re.fullmatch(r"[A-Za-z0-9_-]+", value) for value in ids):
    raise RuntimeError("An assessment ID cannot be safely asserted in a UI selector")
if set(item["assessmentId"] for item in first_records) & set(item["assessmentId"] for item in second_records):
    raise RuntimeError("Cursor page repeated a record from page one")

with open(os.path.join(data_dir, "slack-history-seed.json"), "w", encoding="utf-8") as output:
    json.dump({"workspaceId": workspace, "channelId": channel, "threadId": root,
               "assessmentIds": ids, "internalMessageIds": internal_ids,
               "pageCounts": [len(first_records), len(second_records)]}, output, indent=2)
    output.write("\n")

with open(fixture_path, encoding="utf-8") as source:
    fixture = json.load(source)
fixture["channel"] = {"kind": "Slack", "workspaceId": workspace,
                     "channelId": channel, "threadId": root}
fixture_path_out = os.path.join(data_dir, "trend-history-fixture.json")
with open(fixture_path_out, "w", encoding="utf-8") as output:
    json.dump(fixture, output)
    output.write("\n")

with open(scenario_path, encoding="utf-8") as source:
    scenario = source.read()
marker = "  # RUNTIME_HISTORY_ASSERTIONS\n"
if scenario.count(marker) != 1:
    raise RuntimeError("Trend scenario must contain exactly one dynamic assertion marker")
assertions = "".join(
    "  - type: Expect\n    target: \"text='" + value + "'\"\n    matcher: IsVisible\n"
    for value in ids
)
scenario = scenario.replace(marker, assertions)
with open(os.path.join(data_dir, "console-trend-review-smoke.yaml"), "w", encoding="utf-8") as output:
    output.write(scenario)
print("== local Slack history verified: three distinct assessments across API pages 2+1 ==")
PYEOF
    if [[ "$?" -ne 0 ]]; then
        echo "Could not seed and verify populated local Slack conversation history." >&2
        exit 1
    fi
    unset StyloMail__Slack__SigningSecret
    export STYLOMAIL_SMOKE_DECISION_FILE="$CONSOLE_RUN/data/trend-history-fixture.json"
fi
export ASPNETCORE_URLS=""

if [[ "$driver" == "headless" || "$driver" == "trend-fixture" ]]; then
    mkdir -p "$CONSOLE_RESULTS"
fi

cd "$CONSOLE_REPO" || exit 1
echo "== session artifacts: $CONSOLE_RUN =="
echo "== opening controlled console session ($driver); close it to stop the Host =="
dotnet run --no-build --project src/StyloMail.Desktop -- "${app_args[@]}"
app_status=$?

if [[ "$driver" == "headless" || "$driver" == "trend-fixture" ]]; then
    console_final_status "$CONSOLE_RESULTS/result.json" "$app_status"
    exit $?
fi

exit $app_status
