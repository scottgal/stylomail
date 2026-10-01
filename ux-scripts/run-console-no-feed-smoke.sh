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

# Both of these are set before the harness is sourced, because the harness reads
# them when it starts the Host, and one of them it also *sets*.
#
# `false` is what makes the Hub absent: the Host maps the route only when the
# deployment enabled it, so this is a configuration and not a fault being
# simulated.
export CONSOLE_TRAFFIC=false

# This script's own run directory, and it is no longer assigned here. It used to be
# `export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-no-feed-ux}"` before the
# source, and that assignment is what made `export CONSOLE_RUN=<another run>` delete
# that run: `${CONSOLE_RUN:-...}` keeps an inherited value. The default is handed to
# console_runner_run_dir below the source instead, which refuses an inherited
# directory unless the caller sets CONSOLE_REUSE_RUN=1. An operator can still point
# this run elsewhere, but has to say so.
CONSOLE_RUN_DEFAULT="/tmp/stylomail-console-no-feed-ux"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

# Before the build: console_build_all stamps into $CONSOLE_RUN, so an inherited
# directory has to be refused before it rather than after. See console_runner_run_dir.
console_runner_run_dir "$CONSOLE_RUN_DEFAULT" || exit 2

console_build_all || exit 1

# A different output directory from the main smoke, so a run of one cannot be
# read as evidence about the other. Two scripts writing the same ux-results would
# leave whichever ran last as the only artifact, and the counts would be
# attributed to the wrong script.
#
# The output goes in a subdirectory of ux-results rather than a sibling of it
# because ux-results is already ignored and a new top-level directory would
# dirty the shared tree. That subdirectory is this script's own and it clears it
# on every run, so what is there is always this script's latest and never a
# stale artifact of an earlier one. (The main smoke used to be one of the reasons
# this mattered, because it wiped ux-results wholesale; that wipe is now scoped
# to its own subdirectory, see run-console-smoke.sh.) The scratch directory it
# shares with nothing is set above, before the harness is sourced.
CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/no-feed"

rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

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

# The harness's exit code is not its verdict: a failing script exits 0. Read the
# verdict from result.json before reporting anything.
console_final_status "$CONSOLE_RESULTS/result.json" "$STATUS"
exit $?
