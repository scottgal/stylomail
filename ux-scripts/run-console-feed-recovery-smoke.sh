#!/usr/bin/env bash
# Runs console-feed-recovery-smoke.yaml against a throwaway Host, kills that
# Host in the middle of the run, and brings it back on the same address with the
# same key and the same database.
#
# The fourth of the feed's states and the one nothing else reaches. The main
# smoke proves the console follows a Host that is up. The no-feed smoke proves
# it says so on a deployment that never had a Hub. The drop smoke proves it says
# so when a feed that WAS live stops, and keeps what it had. This one proves the
# console comes back: that it notices the Host returned, and that it reads the
# surface again rather than showing what it last heard.
#
# The claim that costs something is the re-read, and a screen cannot be asked
# whether it re-read. So the runner makes something change while the console is
# blind: it pauses the harness sender over the Host's own control route after
# the Host is back and before the console can reconnect. A change published to
# the Hub while nothing is connected reaches nothing, so the Resume button can
# appear only by the console reading the senders again.
#
# The timing is a schedule, not a guess, and it is the one thing in this file
# that would make the test lie if it were wrong. SignalR's default policy makes
# four attempts and then gives up: at 0, 2, 10 and 30 seconds after the drop,
# so roughly T+0, T+2, T+12 and T+42 for a Host that refuses connections
# instantly. This script restarts the Host at T+20, which is deliberately
# between the third and fourth attempts:
#
#   * after T+12, so the console cannot have reconnected and resynchronised
#     BEFORE the pause is applied. If it did, the pause would arrive as a Hub
#     notice to a live console and the final assertion would pass without the
#     re-read ever happening, which is a test that passes for the wrong reason.
#   * well before T+42, leaving twenty-two seconds for a Host restart and one
#     HTTP call, and leaving the console's last attempt still to come.
#
# The failure mode of a machine slower than that is honest and loud rather than
# silent: the console's last attempt misses, it stops retrying, and the script
# fails on an assertion no retry can satisfy. The runner prints where it was in
# the schedule when that happens, because "recovery did not happen" and "the
# restart was too slow" look identical on the screen.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This script's own scratch directory, set before the harness is sourced because
# the harness sets CONSOLE_RUN itself. A `${CONSOLE_RUN:-...}` default placed
# after the source keeps the harness's value rather than this one, and every
# script then shares the main smoke's directory.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-feed-recovery-ux}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/feed-recovery"

# How long after the drop the Host comes back. See the header: it has to fall
# between SignalR's third and fourth reconnect attempts.
RESTART_AT_SECONDS=20

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

# Its own output directory, because the main smoke wipes ux-results wholesale.
rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, with the live-traffic Hub on =="
export CONSOLE_TRAFFIC=true

# The first start generates the keys, and it is also the only start that may:
# see the restart below.
export CONSOLE_REUSE_KEYS=false
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console, which will have its Host taken away mid-run =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-feed-recovery-smoke.yaml \
    --output "$CONSOLE_RESULTS" &
CONSOLE_DRIVER_PID=$!

# Bounded at two minutes and checked against the console still being alive, so a
# console that died on startup is reported as that rather than as a Host that
# could not be killed.
wait_for_shot() {
    local file="$1" budget="$2" what="$3"

    for _ in $(seq 1 "$budget"); do
        if [[ -f "$file" ]]; then
            return 0
        fi
        if ! kill -0 "$CONSOLE_DRIVER_PID" 2>/dev/null; then
            break
        fi
        sleep 1
    done

    echo "The console never wrote $file, so it never got as far as $what." >&2
    echo "Everything after that point in this run would prove nothing, so this" >&2
    echo "run has failed on its own terms. The result file says where:" >&2
    if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
        cat "$CONSOLE_RESULTS/result.json" >&2
    fi
    exit 1
}

echo "== waiting for the console to report Live =="
wait_for_shot "$CONSOLE_RESULTS/01-feed-live.png" 120 "following the Host"

echo "== the console is Live; stopping the Host =="
DROP_AT=$(date +%s)
console_stop_host

wait_for_shot "$CONSOLE_RESULTS/02-feed-dropped.png" 90 "noticing the Host went away"

# The schedule from the header. Sleeping to an absolute instant rather than for
# a duration, so the Host restart lands where it was designed to land even if
# the console took a second longer to notice the drop than expected.
NOW=$(date +%s)
RESUME_AT=$((DROP_AT + RESTART_AT_SECONDS))
if (( RESUME_AT > NOW )); then
    echo "== waiting $((RESUME_AT - NOW))s, to land the restart between the console's third and fourth attempts =="
    sleep $((RESUME_AT - NOW))
else
    echo "The console took $((NOW - DROP_AT))s to write the dropped screenshot, which is" >&2
    echo "already past the T+${RESTART_AT_SECONDS} restart point this run is built on." >&2
    echo "Restarting now, but the console may already have made its last attempt" >&2
    echo "and given up." >&2
fi

echo "== bringing the Host back on the same address, key and database =="
# The same Host twice, which is three things and none of them optional.
#
# CONSOLE_REUSE_KEYS keeps the principal key the first start wrote, so the key
# the console holds is still accepted. The database was never generated per-run
# in the first place; it is named at $CONSOLE_RUN/data/host.db, so this second
# start opens the one the first start used, and the sender the console is
# watching is still there to be changed. Without either, this would be a test of
# a second deployment wearing the first one's address.
export CONSOLE_REUSE_KEYS=true
export CONSOLE_HOST_LOG="$CONSOLE_RUN/host-restart.log"
console_start_host || exit 1

echo "== pausing the harness sender, with the console still unable to hear it =="
console_pause_sender harness || exit 1

echo "== waiting for the console to come back and re-read =="
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
