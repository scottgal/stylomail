#!/usr/bin/env bash
# Runs console-feed-drop-smoke.yaml against a throwaway Host, and kills that
# Host in the middle of the run.
#
# The third of the feed's three states, and the only one that has to be caused.
# run-console-smoke.sh proves the console follows a Host that is up;
# run-console-no-feed-smoke.sh proves it says so on a deployment that never had
# a Hub. Neither reaches the state where a feed that WAS live stops, which is
# the one an operator can be misled by, because it is the state in which the
# screen looks exactly like it did while everything was fine.
#
# The yaml cannot do this on its own: the harness has no shell-out action, so
# nothing in a script can stop a process. The drop has to come from outside,
# which is what this file is for.
#
# The timing is not a sleep. The yaml takes a screenshot the moment it has
# asserted the console is Live, and this script waits for that file to appear
# before killing the Host. So the kill lands after the console is provably
# following the Host, rather than after a delay generous enough to usually cover
# a cold start. A fixed sleep here would be the one thing that makes this test
# flaky, and flaky is the same as absent for a proof.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This script's own run directory, and it is no longer assigned here. It used to be
# `export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-feed-drop-ux}"` before
# the source, and that is what made `export CONSOLE_RUN=<another run>` delete that
# run: `${CONSOLE_RUN:-...}` keeps an inherited value. The default goes to
# console_runner_run_dir below the source instead, which refuses an inherited
# directory unless the caller sets CONSOLE_REUSE_RUN=1, and clears it otherwise.
CONSOLE_RUN_DEFAULT="/tmp/stylomail-console-feed-drop-ux"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/feed-drop"

console_stop_console() {
    if [[ -n "${CONSOLE_DRIVER_PID:-}" ]]; then
        kill "$CONSOLE_DRIVER_PID" 2>/dev/null || true
    fi
}

cleanup() {
    console_stop_console
    console_stop_host
}
trap cleanup EXIT INT TERM

# Before the build: console_build_all stamps into $CONSOLE_RUN, so an inherited
# directory has to be refused before it rather than after. See console_runner_run_dir.
console_runner_run_dir "$CONSOLE_RUN_DEFAULT" || exit 2

console_build_all || exit 1

# Its own output directory too: sharing one would mean one run's artifacts
# standing in for another's, because the harness clears nothing it wrote before.
# This script clears its own subdirectory every time instead. The scratch
# directory is set above, before the harness is sourced.
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, with the live-traffic Hub on =="
# The Hub is the default here, but stated rather than inherited: this script
# only means anything on a Host that offers a feed, and an environment variable
# left set by whoever ran the no-feed script first would turn this into a test
# of the wrong thing without failing.
export CONSOLE_TRAFFIC=true
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console, which will have its Host taken away mid-run =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-feed-drop-smoke.yaml \
    --output "$CONSOLE_RESULTS" &
CONSOLE_DRIVER_PID=$!

# Wait for the console to have proven it is live, then pull the Host out from
# under it. Bounded at two minutes and checked against the console still being
# alive, so a console that died on startup is reported as that rather than as
# a Host that could not be killed.
echo "== waiting for the console to report Live =="
LIVE_FILE="$CONSOLE_RESULTS/01-feed-live.png"
for _ in $(seq 1 120); do
    if [[ -f "$LIVE_FILE" ]]; then
        break
    fi
    if ! kill -0 "$CONSOLE_DRIVER_PID" 2>/dev/null; then
        break
    fi
    sleep 1
done

if [[ ! -f "$LIVE_FILE" ]]; then
    echo "The console never reached the Live screenshot, so it never got as far" >&2
    echo "as following the Host. Killing the Host now would prove nothing, so" >&2
    echo "this run has failed on its own terms. The result file says where:" >&2
    if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
        cat "$CONSOLE_RESULTS/result.json" >&2
    fi
    exit 1
fi

echo "== the console is Live; stopping the Host =="
console_stop_host

wait "$CONSOLE_DRIVER_PID"
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
