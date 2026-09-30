#!/usr/bin/env bash
# Runs console-no-feed-smoke.yaml against a throwaway Host with its live-traffic
# Hub NOT mapped, then takes it down again.
#
# The other half of the feed's proof. run-console-smoke.sh starts a Host with
# the Hub enabled and asserts the console says "Live"; this starts one without
# it, which is what a real deployment gets by default, and asserts the console
# says so rather than looking broken. Both are real configurations and both are
# run rather than assumed.
#
# Run this, not the yaml directly, for the same reason as the other script: this
# one owns the Host the assertions are about and deletes it afterwards.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Set before the harness is sourced, because the harness reads it when it starts
# the Host. `false` here is what makes the Hub absent: the Host maps the route
# only when the deployment enabled it, so this is a configuration and not a
# fault being simulated.
export CONSOLE_TRAFFIC=false

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_build_all || exit 1

# A different scratch directory and output directory from the main smoke, so a
# run of one cannot be read as evidence about the other. Two scripts writing the
# same ux-results would leave whichever ran last as the only artifact, and the
# counts would be attributed to the wrong script.
#
# The output goes in a subdirectory of ux-results rather than a sibling of it
# because ux-results is already ignored and a new top-level directory would
# dirty the shared tree. The main smoke wipes ux-results wholesale, so running
# it after this one leaves these artifacts to be regenerated rather than stale:
# this script clears its own subdirectory on every run.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-no-feed-ux}"
CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/no-feed"

rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, with no live-traffic Hub =="
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-no-feed-smoke.yaml \
    --output "$CONSOLE_RESULTS"
STATUS=$?

echo
echo "== result =="
if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
    cat "$CONSOLE_RESULTS/result.json"
fi
echo
echo "screenshots in $CONSOLE_RESULTS/"

exit $STATUS
