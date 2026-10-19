#!/usr/bin/env bash
# Runs the Review, Policy and Images pages against a throwaway Host.
#
# One accepted held message gives the Review page a real held count; the Host
# has no policy pack and no proxy composition, so Policy and Images show their
# named empty states.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

export CONSOLE_PROVIDER=nimble
CONSOLE_MESSAGE="$CONSOLE_REPO/tests/StyloMail.Desktop.Tests/fixtures/nimble-held-message.eml"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_runner_run_dir "/tmp/stylomail-console-management" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-management"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

echo "== seeding a company and one held message =="
console_seed_management
console_seed_submission \
    "$CONSOLE_MESSAGE" \
    "billing@paypa1-secure.top" \
    "alice@example.test" || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the management pages =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-management-smoke.yaml \
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
