#!/usr/bin/env bash
# Drives the people directory and one real Mail queue against a throwaway Host.
#
# The Host needs an assessor that accepts a submission, so this uses the local
# model the nimble runner already requires. The accepted message is what puts a
# real row in the held queue and a correspondent in the directory.

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

console_runner_run_dir "/tmp/stylomail-console-slice2" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-slice2"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

echo "== seeding a company and the harness principal =="
console_seed_management

echo "== submitting one message through POST /v1/submissions =="
console_seed_submission \
    "$CONSOLE_MESSAGE" \
    "billing@paypa1-secure.top" \
    "alice@example.test" || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the recipients and mail pages =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-slice2-smoke.yaml \
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
