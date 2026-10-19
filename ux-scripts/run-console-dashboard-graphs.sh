#!/usr/bin/env bash
# Graph-focused dashboard capture over a small governed set of real submissions.
#
# Three submissions give the assessments chart nonzero buckets and the outcome
# mix a real bar. The frame is harness data and is labelled as such.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

export CONSOLE_PROVIDER=nimble
CONSOLE_HELD="$CONSOLE_REPO/tests/StyloMail.Desktop.Tests/fixtures/nimble-held-message.eml"
CONSOLE_QUARANTINE="$CONSOLE_REPO/tests/StyloMail.Desktop.Tests/fixtures/quarantine-threshold-payment-change.eml"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_runner_run_dir "/tmp/stylomail-console-graphs" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-graphs"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

echo "== seeding a small governed set of submissions =="
console_seed_management

console_seed_submission \
    "$CONSOLE_HELD" \
    "billing@paypa1-secure.top" \
    "alice@example.test" \
    "graph-held-1-$$" || exit 1

console_seed_submission \
    "$CONSOLE_HELD" \
    "alerts@example.test" \
    "bob@example.test" \
    "graph-held-2-$$" || exit 1

console_seed_submission \
    "$CONSOLE_QUARANTINE" \
    "alerts@barclays-secure-verify.top" \
    "carol@example.test" \
    "graph-quarantine-$$" || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the graph-focused dashboard =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-dashboard-graphs.yaml \
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
