#!/usr/bin/env bash
# Runs the Service page against a throwaway Host.
#
# The default harness Host has no proxy composition, which is the shape this run
# asserts: empty accounts and listeners are named, session and operation telemetry
# is named as not produced, and queue and storage come from the real summaries.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_runner_run_dir "/tmp/stylomail-console-service" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-service"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the Service page =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-service-smoke.yaml \
    --output "$CONSOLE_RESULTS"
STATUS=$?

echo
echo "== result =="
if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
    cat "$CONSOLE_RESULTS/result.json"
fi
echo
echo "screenshots in $CONSOLE_RESULTS/"

console_final_status "$CONSOLE_RESULTS/result.json" "$STATUS"
exit $?
