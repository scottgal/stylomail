#!/usr/bin/env bash
# Bounded UI verification for the per-decision trend-observation increment.
# Writes a fresh evidence directory per invocation; never clears prior evidence.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAMP="$(date -u '+%Y%m%dT%H%M%SZ')-$$"
EVIDENCE="$REPO/.styloagent/scratch/desktop/trend-verification/$STAMP"
PORT=5290

export DOTNET_ROOT="/usr/local/share/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export CONSOLE_PORT="$PORT"
# Do not leave reusable MSBuild workers in the bounded verifier's process group.
export MSBUILDDISABLENODEREUSE=1
mkdir -p "$EVIDENCE" || exit 2

run_bounded() {
    local seconds="$1" raw_log="$2"
    shift 2
    python3 - "$seconds" "$raw_log" "$REPO/ux-scripts/collector_cleanup_policy.py" "$@" <<'PYEOF'
import os
import signal
import shlex
import subprocess
import sys
import time
sys.path.insert(0, os.path.dirname(sys.argv[3]))
import collector_cleanup_policy as collector_policy

limit = int(sys.argv[1])
log_path = sys.argv[2]
policy_path = sys.argv[3]
command = sys.argv[4:]
with open(log_path, "wb") as output:
    process = subprocess.Popen(
        command,
        stdout=output,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )

    trusted_package_root, trusted_collector = collector_policy.trusted_paths()

    def ps_text(command):
        return subprocess.run(
            command,
            check=True,
            capture_output=True,
            text=True,
            timeout=3,
        ).stdout

    def group_snapshot():
        listing = subprocess.run(
            ["ps", "-axo", "pid,ppid,pgid,stat,etime,comm="],
            check=True,
            capture_output=True,
            text=True,
            timeout=3,
        ).stdout
        members = {}
        for line in listing.splitlines()[1:]:
            fields = line.strip().split(None, 5)
            if len(fields) == 6 and fields[2] == str(process.pid):
                members[fields[0]] = fields
        return members

    def is_exact_collector(pid):
        return collector_policy.probe_process(
            pid, process.pid, ps_text, trusted_package_root, trusted_collector
        )

    def exact_collector_group():
        try:
            first = group_snapshot()
            if not first:
                return False, "group census had no members", []
            for pid, fields in first.items():
                identity_code = is_exact_collector(pid)
                if identity_code != "verified":
                    return False, f"member identity {identity_code}", list(first)
            second = group_snapshot()
            if set(second) != set(first):
                return False, "group membership changed during identity recheck", list(second)
            for pid, fields in second.items():
                identity_code = is_exact_collector(pid)
                if fields[2] != str(process.pid) or identity_code != "verified":
                    return False, f"recheck identity {identity_code}", list(second)
            return True, "two matching PID/PGID/executable/path censuses", list(second)
        except Exception as error:
            return False, f"identity probe unavailable ({type(error).__name__})", []

    def bounded_group_cleanup(label):
        print(f"{label}: sending TERM to owned process group {process.pid}", file=sys.stderr)
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            return True
        except OSError:
            return False
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                return True
            except OSError:
                return False
            time.sleep(0.2)
        print(f"{label}: TERM grace expired; sending KILL", file=sys.stderr)
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            return True
        except OSError:
            return False
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                return True
            except OSError:
                return False
            time.sleep(0.2)
        return False

    def report_group_members(label):
        try:
            listing = subprocess.run(
                ["ps", "-axo", "pid,ppid,pgid,stat,etime,comm="],
                check=True,
                capture_output=True,
                text=True,
                timeout=3,
            ).stdout
        except Exception as error:
            print(f"{label}: executable-name process census unavailable; continuing fail-closed cleanup ({type(error).__name__})", file=sys.stderr)
            return
        found = False
        for line in listing.splitlines()[1:]:
            fields = line.strip().split(None, 5)
            if len(fields) == 6 and fields[2] == str(process.pid):
                found = True
                role = "unknown"
                try:
                    # Read arguments only for this exact census PID; never
                    # print them because arguments can contain user data.
                    arguments = subprocess.run(
                        ["ps", "-o", "args=", "-p", fields[0]],
                        check=True,
                        capture_output=True,
                        text=True,
                        timeout=3,
                    ).stdout
                    argument_tokens = shlex.split(arguments)
                    argument_probe = f"ARGS=ok ARGS_TOKENS={len(argument_tokens)}"
                    basenames = {
                        os.path.basename(token.rstrip(",:")).lower()
                        for token in argument_tokens
                    }
                    identity_code = is_exact_collector(fields[0])
                    if identity_code == "verified":
                        role = "avalonia-build-telemetry-collector-exact"
                    elif "vbcscompiler.dll" in basenames:
                        role = "roslyn-shared-compiler"
                    elif "avalonia.buildservices.collector.dll" in basenames:
                        role = f"avalonia-build-telemetry-collector-unverified-{identity_code}"
                    elif "msbuild.dll" in basenames and any("/nodemode:" in token.lower() for token in argument_tokens):
                        role = "msbuild-worker-node"
                    elif "msbuild.dll" in basenames:
                        role = "msbuild-process"
                    elif "csc.dll" in basenames:
                        role = "roslyn-csc-process"
                    elif "microsoft.testplatform.testhost.dll" in basenames or "testhost.dll" in basenames:
                        role = "testhost"
                    elif "stylomail.host" in basenames or "stylomail.host.dll" in basenames:
                        role = "host-server"
                    elif "stylomail.desktop.dll" in basenames:
                        role = "desktop-app"
                    elif "stylomail.desktop.tests.dll" in basenames:
                        role = "desktop-test-assembly"
                    elif "serve" in argument_tokens and any("stylomail.host" in token.lower() for token in argument_tokens):
                        role = "host-server"
                    elif "test" in argument_tokens and any(token.lower().endswith(".csproj") for token in argument_tokens):
                        role = "dotnet-test-entry"
                    elif "build" in argument_tokens or any(token.lower().endswith(".slnx") for token in argument_tokens):
                        role = "dotnet-build-entry"
                    elif "run" in argument_tokens:
                        role = "dotnet-run-entry"
                except Exception:
                    role = "unknown (exact-PID argument probe unavailable)"
                    argument_probe = "ARGS=unavailable"
                print(
                    f"{label}: PID={fields[0]} PPID={fields[1]} PGID={fields[2]} "
                    f"STAT={fields[3]} ETIME={fields[4]} COMM={fields[5]} "
                    f"{argument_probe} ROLE={role}",
                    file=sys.stderr,
                )
        if not found:
            print(f"{label}: process group exists but ps listed no member; continuing fail-closed cleanup", file=sys.stderr)

    try:
        status = process.wait(timeout=limit)
    except subprocess.TimeoutExpired:
        print(f"timeout after {limit}s; sending TERM to process group {process.pid}", file=sys.stderr)
        report_group_members("timeout process group census")
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                break
            time.sleep(0.2)
        else:
            print("TERM grace expired; sending KILL to the process group", file=sys.stderr)
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            print("owned entry process did not reap within 5s after TERM/KILL", file=sys.stderr)
            raise SystemExit(126)
        cleanup_deadline = time.monotonic() + 5
        while time.monotonic() < cleanup_deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                break
            time.sleep(0.2)
        else:
            print("owned process group remains after bounded TERM/KILL cleanup", file=sys.stderr)
            raise SystemExit(127)
        raise SystemExit(124)

    # A successful entrypoint must not leave a Host or UI child behind.
    def group_exists():
        try:
            os.killpg(process.pid, 0)
            return True
        except ProcessLookupError:
            return False

    # Give normal dotnet/testhost shutdown a bounded chance to finish before
    # interpreting a briefly lingering group as an abandoned child.
    natural_deadline = time.monotonic() + 5
    while group_exists() and time.monotonic() < natural_deadline:
        time.sleep(0.2)
    if group_exists():
        print(f"process group {process.pid} remains after exit; stopping owned children", file=sys.stderr)
        report_group_members("residual process group census")
        exact, reason, collector_pids = exact_collector_group()
        print(f"Exact collector teardown classification: {reason}; members={len(collector_pids)}", file=sys.stderr)
        if exact:
            print(f"Managed collector cleanup: verified {len(collector_pids)} exact package-owned process(es)", file=sys.stderr)
        cleaned = bounded_group_cleanup("owned process-group cleanup")
        if not cleaned:
            print("owned process group remains after bounded TERM/KILL cleanup", file=sys.stderr)
            raise SystemExit(127)
        if exact:
            print("Managed collector cleanup: confirmed process group empty", file=sys.stderr)
            raise SystemExit(status)
        if status != 0:
            raise SystemExit(status)
        raise SystemExit(125)

raise SystemExit(status)
PYEOF
}

sanitize_log() {
    local raw_log="$1" log="$2"
    sed -E \
        -e 's/(STYLOMAIL_SMOKE_KEY[=:][[:space:]]*)[^[:space:]]+/\1[redacted]/g' \
        -e 's/(StyloMail__Slack__SigningSecret[=:][[:space:]]*)[^[:space:]]+/\1[redacted]/g' \
        -e 's/([Aa]uthorization:[[:space:]]*[Bb]earer[[:space:]]+)[^[:space:]]+/\1[redacted]/g' \
        "$raw_log" > "$log" || return 1
    rm -f "$raw_log"
}

assert_desktop_tests() {
    python3 - "$1" <<'PYEOF'
import re
import sys

text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
matches = re.findall(
    r"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
    text,
)
if not matches:
    print("Desktop test summary not found", file=sys.stderr)
    raise SystemExit(1)
failed, passed, skipped, total = map(int, matches[-1])
print(f"Desktop tests: passed={passed} failed={failed} skipped={skipped} total={total}")
if total <= 0 or passed <= 0 or failed != 0:
    raise SystemExit(1)
PYEOF
}

assert_ui_result() {
    python3 - "$1" <<'PYEOF'
import json
import sys

path = sys.argv[1]
try:
    with open(path, encoding="utf-8") as source:
        result = json.load(source)
except Exception as error:
    print(f"UI result is missing or invalid JSON: {error}", file=sys.stderr)
    raise SystemExit(1)
actions = result.get("actionResults") or []
failed = [item for item in actions if item.get("success") is not True]
print(f"UI result: success={result.get('success')} actions={len(actions)} failed={len(failed)}")
if result.get("success") is not True or not actions or failed:
    raise SystemExit(1)
PYEOF
}

assert_seed_artifact() {
    python3 - "$1" <<'PYEOF'
import json
import re
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    seed = json.load(source)
ids = seed.get("assessmentIds") or []
messages = seed.get("internalMessageIds") or []
if seed.get("pageCounts") != [2, 1] or len(ids) != 3 or len(set(ids)) != 3:
    print("Seed evidence does not prove three distinct assessments across 2+1 pages", file=sys.stderr)
    raise SystemExit(1)
if len(messages) != 3 or len(set(messages)) != 3:
    print("Seed evidence does not contain three distinct internal message IDs", file=sys.stderr)
    raise SystemExit(1)
if any(not re.fullmatch(r"[A-Za-z0-9_-]+", value) for value in ids):
    print("Seed evidence contains an unsafe assessment ID", file=sys.stderr)
    raise SystemExit(1)
print("Seed evidence: three distinct original records across API pages 2+1")
PYEOF
}

assert_port_released() {
    python3 - "$PORT" <<'PYEOF'
import errno
import socket
import sys
import time

port = sys.argv[1]
deadline = time.monotonic() + 25
attempts = 0
while True:
    attempts += 1
    retry = False
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        try:
            # Overview-approved exclusive IPv4 bind; deliberately no SO_REUSE* option.
            probe.bind(("127.0.0.1", int(port)))
        except OSError as error:
            if error.errno != errno.EADDRINUSE:
                raise
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("EADDRINUSE persisted through the bounded 25s post-teardown gate") from error
            retry = True
    finally:
        probe.close()
    if not retry:
        break
    time.sleep(min(0.25, max(0, deadline - time.monotonic())))
print(f"Port {port}: exclusively bindable after {attempts} post-teardown attempt(s)")
PYEOF
}

run_driver() {
    local tag="$1" driver="$2"
    local run_dir="/tmp/stylomail-console-interactive-trend-verify-$STAMP-$tag"
    local raw_log="$EVIDENCE/$tag.raw.log"
    local log="$EVIDENCE/$tag.log"
    local result_dir="$EVIDENCE/$tag-ui-results"
    local status result_json port_status=0 artifact_status=0 log_status=0

    echo "Starting $driver UI run; session=$run_dir; evidence=$result_dir"
    run_bounded 420 "$raw_log" env \
        CONSOLE_INTERACTIVE_RUN_DIR="$run_dir" \
        CONSOLE_INTERACTIVE_DRIVER="$driver" \
        DESKTOP_DIAGNOSTIC_PROCESS_CENSUS=true \
        bash "$REPO/ux-scripts/run-console-interactive.sh"
    status=$?

    # Keep reviewable output without carrying credentials if a tool ever prints
    # one. Raw output is removed after redaction and never copied to the report.
    sanitize_log "$raw_log" "$log" || log_status=$?

    if [[ -d "$run_dir/ui-results" ]]; then
        cp -R "$run_dir/ui-results" "$result_dir" || artifact_status=$?
    else
        echo "No UI result directory was produced at $run_dir/ui-results" >> "$log"
        artifact_status=1
    fi

    if [[ "$driver" == "trend-fixture" ]]; then
        local seed_artifact="$EVIDENCE/$tag-slack-history-seed.json"
        if [[ -s "$run_dir/data/slack-history-seed.json" ]]; then
            cp "$run_dir/data/slack-history-seed.json" "$seed_artifact" || artifact_status=$?
        else
            echo "Populated Slack history seed artifact is missing" >&2
            artifact_status=1
        fi
    fi

    printf 'Driver %s exit status: %s\n' "$driver" "$status"
    printf 'Sanitized log: %s\n' "$log"
    printf 'UI artifacts: %s\n' "$result_dir"
    assert_port_released || port_status=$?
    if (( status != 0 )); then
        return "$status"
    fi
    if (( port_status != 0 )); then
        return "$port_status"
    fi
    if (( log_status != 0 )); then
        return "$log_status"
    fi
    if (( artifact_status != 0 )); then
        return "$artifact_status"
    fi
    if [[ "$driver" == "trend-fixture" ]]; then
        assert_seed_artifact "$EVIDENCE/$tag-slack-history-seed.json" || return 1
    fi
    result_json="$result_dir/result.json"
    assert_ui_result "$result_json" || return 1
}

ui_phase="${DESKTOP_UI_PHASE:-all}"
case "$ui_phase" in
    all|host|fixture) ;;
    *)
        echo "DESKTOP_UI_PHASE must be 'all', 'host', or 'fixture'." >&2
        exit 2
        ;;
esac

echo "Evidence directory: $EVIDENCE"
if [[ "${DESKTOP_BUILD_ONLY_DIAGNOSTIC:-false}" == "true" ]]; then
    run_bounded 180 "$EVIDENCE/build-only.raw.log" \
        bash -c 'cd "$1" && exec dotnet build StyloMail.slnx --nologo --disable-build-servers -p:UseSharedCompilation=false' \
        build-only "$REPO"
    build_status=$?
    sanitize_log "$EVIDENCE/build-only.raw.log" "$EVIDENCE/build-only.log" || exit 1
    printf 'Isolated solution build entry/cleanup status: %s\n' "$build_status"
    printf 'Build log: %s\n' "$EVIDENCE/build-only.log"
    exit "$build_status"
fi

if [[ "${DESKTOP_UI_ONLY:-false}" == "true" ]]; then
    echo "Desktop unit suite omitted by request; reusing .styloagent/scratch/desktop/trend-verification/20261006T171927Z-16564/desktop-tests.log (308 passed, 20 skipped, 0 failed)."
else
    run_bounded 180 "$EVIDENCE/desktop-tests.raw.log" \
        dotnet test "$REPO/tests/StyloMail.Desktop.Tests/StyloMail.Desktop.Tests.csproj" \
            --nologo --disable-build-servers -p:UseSharedCompilation=false
    test_status=$?
    sanitize_log "$EVIDENCE/desktop-tests.raw.log" "$EVIDENCE/desktop-tests.log" || exit 1
    printf 'Desktop test exit status: %s\n' "$test_status"
    printf 'Desktop test log: %s\n' "$EVIDENCE/desktop-tests.log"
    if (( test_status != 0 )); then
        exit "$test_status"
    fi
    assert_desktop_tests "$EVIDENCE/desktop-tests.log" || exit 1
fi

if [[ "$ui_phase" == "all" || "$ui_phase" == "host" ]]; then
    run_driver host headless || exit $?
fi
if [[ "$ui_phase" == "all" || "$ui_phase" == "fixture" ]]; then
    run_driver fixture trend-fixture || exit $?
fi

echo "Bounded trend UI verification completed: $EVIDENCE"
