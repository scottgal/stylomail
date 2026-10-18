#!/usr/bin/env bash
# Builds a schema with a throwaway Host, seeds only its stopped temp DB, then captures via real routes.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/console-harness.sh"
cleanup() { console_stop_host; }
trap cleanup EXIT INT TERM

console_runner_run_dir "$CONSOLE_REPO/.styloagent/scratch/desktop/profile-history-capture" || exit 2
console_build_all || exit 1
echo "== bootstrap schema with a temporary Host =="
console_start_host || exit 1
console_stop_host
unset CONSOLE_HOST_PID
echo "== seed stopped temporary database (harness-only direct rows) =="
python3 "$HERE/seed-profile-history.py" "$CONSOLE_RUN/data/host.db"
echo "== start governed Host and capture normal console routes =="
export CONSOLE_REUSE_KEYS=true
console_start_host || exit 1
console_export_app_env
RESULTS="$CONSOLE_REPO/ux-results/console-profile-history"
rm -rf "$RESULTS" && mkdir -p "$RESULTS"
(cd "$CONSOLE_REPO" && dotnet run --project src/StyloMail.Desktop -- --ux-headless --ux-test --script ux-scripts/console-profile-history-capture.yaml --output "$RESULTS")
status=$?
console_final_status "$RESULTS/result.json" "$status"
