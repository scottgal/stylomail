#!/usr/bin/env bash
# Runs the dashboard-first console against a throwaway Host.
#
# This is the narrow verification for the dashboard slice: it starts its own
# Host, reads the four summary routes through the shipped client, and asserts
# the card labels and the queue row. It is not a production capture and the
# frame it writes is labelled harness data.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

# The runner's own run directory. The harness deletes it, so it must not be a
# directory another run owns.
console_runner_run_dir "/tmp/stylomail-console-dashboard" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-dashboard"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the dashboard =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-dashboard-smoke.yaml \
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
