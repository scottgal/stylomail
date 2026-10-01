#!/usr/bin/env bash
# Runs console-operator-retry-smoke.yaml against a throwaway Host, kills that
# Host, and then leaves it away for the whole of the operator's retry.
#
# The fourth of the feed's states, and the only one a person causes. The other
# three are proved elsewhere: run-console-smoke.sh follows a Host that is up,
# run-console-no-feed-smoke.sh covers a deployment that never had a Hub, and
# run-console-feed-drop-smoke.sh covers a feed that was live and stopped. What
# none of them reaches is what happens when an operator asks for the connection
# back and the Host is still not there.
#
# The shape of it is the feed-drop script's, and for the same reason: the drop
# has to be caused from outside, because the harness has no shell-out action, so
# this file kills the Host. The timing is a fact rather than a sleep - the yaml
# writes a screenshot the moment it has asserted the console is Live, and this
# script waits for that file before killing anything.
#
# What it deliberately does NOT do is bring the Host back. A run where the Host
# returns would be a run where the connect succeeds on its first attempt, which
# is console-long-outage-smoke.sh, and it would exercise none of this. Here the
# console spends its whole sequence knocking at an address with nothing behind
# it, which is the only arrangement in which the bound and the visibility of that
# bound can be seen at all.
#
# Unlike the long-outage run, nothing here races a restart: the only waits are
# the console's own, and the one number this script adds is the bound on how long
# it will wait for the console to finish driving.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This script's own scratch directory, set before the harness is sourced because
# the harness assigns CONSOLE_RUN itself. A `${CONSOLE_RUN:-...}` default placed
# after the source keeps the harness's value, and every script then shares the
# main smoke's directory.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-operator-retry-ux}"

# This lane's port, stated rather than inherited. 5271 is the harness's own
# default and belongs to no lane in particular, so a run that took it could have
# been talking to a Host another lane started; 5290 is the console lane's, and
# saying it here is what keeps this run's Host and this run's console on one
# address that no other script uses.
export CONSOLE_PORT="${CONSOLE_PORT:-5290}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/operator-retry"

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

console_build_all || exit 1

# Its own output directory, because the harness clears nothing it wrote before:
# two runners sharing one would leave whichever ran last as the only artifacts.
rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, with the live-traffic Hub on =="
# Stated rather than inherited: this run only means anything on a Host that
# offers a feed, and a variable left set by whoever ran the no-feed script first
# would turn it into a test of the wrong thing without failing.
export CONSOLE_TRAFFIC=true
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console, which will lose its Host and then be asked to reconnect to it =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-operator-retry-smoke.yaml \
    --output "$CONSOLE_RESULTS" &
CONSOLE_DRIVER_PID=$!

# Wait for the console to have proven it is live, then pull the Host out from
# under it. Bounded and checked against the console still being alive, so a
# console that died on startup is reported as that rather than as a Host that
# could not be killed.
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

echo "== the console is Live; stopping the Host, and it stays stopped for this run =="
console_stop_host

# The script runs for about ninety seconds after this point: 52 past the
# automatic budget, then the operator's own sequence, then enough to see it stop.
# Nothing here is timed against it, so this is only a bound that turns a console
# that hung into a failure with a readable result rather than a run that never
# ends.
echo "== waiting for the driver to finish (~90s of waits, all of them the console's) =="
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
