#!/usr/bin/env bash
# Runs console-long-outage-smoke.yaml against a throwaway Host, takes that Host
# away for longer than SignalR's retry budget, brings it back, and then leaves it
# to the operator's own Reconnect - which is the thing that did not exist before
# 1ce74d7.
#
# The sibling of run-console-feed-recovery-smoke.sh, and the two are a pair. That
# script proves the console heals itself when the Host returns inside the budget,
# and it is deliberately timed to land the restart before the console's last
# attempt. This one is the other ending: the Host stays away past the last
# attempt, the console gives up for good, and the only way back is a person.
# Neither run reaches the other's state, which is why both exist.
#
# The schedule, and it is a schedule rather than a sleep:
#
#   T+0    the console is Live; the script writes 01-feed-live and this runner
#          stops the Host. The clock starts at the drop, not at the start.
#   T+1    the console has detected the gap and says so; the script waits 52s,
#          which puts the end of the wait at roughly T+53.
#   T+42   SignalR's fourth and final attempt (0, 2, 10, 30 second delays). The
#          wait is sized so that every attempt is behind the assertion that
#          follows it: the console has no retry left to make.
#   T+53   the script asserts the feed is still down and writes
#          03-still-down-after-budget, which is this runner's cue.
#   T+53+  this runner restarts the Host on the same address, key and database,
#          waits for it to answer, and pauses the harness sender over the Host's
#          own control route.
#   T+173  the end of the script's second wait, which exists to cover the
#          restart above. Then it asserts the console is STILL not following the
#          Host, which is the stronger half of the claim: the Host is there and
#          the console does not care, because it has stopped retrying.
#   then   the script clicks Reconnect and requires Live plus the re-read.
#
# The second wait is the one thing here that could slip, so this runner times
# the restart, stamps every phase, and FAILS THE RUN if the restart did not fit
# the script's wait rather than letting the console be blamed for it. That is
# not defensive: a click that lands while the Host is still away leaves the feed
# in its Unreachable state, which retries nothing, so the run would fail ninety
# seconds later on an assertion about the feed.
#
# It has slipped once. Measured on this machine: a restart costs about 11s on a
# quiet box, and the first run of this file measured 80s for the restart and the
# pause, on a machine carrying load ~20 from the rest of the fleet. The script's
# wait is 120s for that reason, and this guard is what makes a worse day loud
# instead of confusing.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This script's own scratch directory, set before the harness is sourced because
# the harness assigns CONSOLE_RUN itself. A `${CONSOLE_RUN:-...}` default placed
# after the source keeps the harness's value, and every script then shares the
# main smoke's directory.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-long-outage-ux}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/long-outage"

# The two waits in console-long-outage-smoke.yaml, named here only so this
# runner can compare them against what it measured. They are the script's
# numbers; this file does not set them. If one is edited, the other has to be,
# and the mismatch is reported rather than assumed away.
SCRIPT_WAIT_BEFORE_ASSERTING_MS=52000
SCRIPT_WAIT_FOR_THE_RESTART_MS=120000

# A stamp on every phase, because the one thing a reader of a failed run needs
# is when each phase happened. The first run of this file reported an 80s
# restart and there was no way to see where the time had gone.
stamp() { date +%H:%M:%S; }

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

echo "[$(stamp)] == starting the throwaway Host on $CONSOLE_BASE, with the live-traffic Hub on =="
export CONSOLE_TRAFFIC=true

# The first start generates the keys, and it is also the only start that may:
# see the restart below.
export CONSOLE_REUSE_KEYS=false
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "[$(stamp)] == driving the console, which will have its Host taken away for good =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-long-outage-smoke.yaml \
    --output "$CONSOLE_RESULTS" &
CONSOLE_DRIVER_PID=$!

# Bounded and checked against the console still being alive, so a console that
# died on startup is reported as that rather than as a Host that could not be
# killed.
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

echo "[$(stamp)] == waiting for the console to report Live =="
wait_for_shot "$CONSOLE_RESULTS/01-feed-live.png" 120 "following the Host"

echo "[$(stamp)] == the console is Live; stopping the Host, which will not be back inside its budget =="
DROP_AT=$(date +%s)
console_stop_host

wait_for_shot "$CONSOLE_RESULTS/02-feed-dropped.png" 90 "noticing the Host went away"

# The script is now in its long wait. This runner waits with it, and the cue for
# the restart is the screenshot the script writes after asserting the console
# has not recovered: a restart this runner started earlier would be a restart
# the console might still have retried into, which is exactly the state this
# script is built to exclude.
echo "[$(stamp)] == waiting for the script to reach the end of the retry budget (~$((SCRIPT_WAIT_BEFORE_ASSERTING_MS / 1000))s) =="
wait_for_shot "$CONSOLE_RESULTS/03-still-down-after-budget.png" 180 "asserting the console has stopped retrying"

RESTART_CUE_AT=$(date +%s)
echo "[$(stamp)] == the console has given up; bringing the Host back on the same address, key and database =="
# The same Host twice, which is three things and none of them optional.
#
# CONSOLE_REUSE_KEYS keeps the principal key the first start wrote, so the key
# the console has stored is still accepted. The database was never generated
# per-run in the first place; it is named at $CONSOLE_RUN/data/host.db, so this
# second start opens the one the first start used, and the sender the console is
# watching is still there to be changed. Without either, this would be a test of
# a second deployment wearing the first one's address.
export CONSOLE_REUSE_KEYS=true
export CONSOLE_HOST_LOG="$CONSOLE_RUN/host-restart.log"
console_start_host || exit 1

echo "[$(stamp)] == pausing the harness sender, with the console still unable to hear it =="
# Nothing is connected to the Hub at this moment: the console's socket closed
# when the budget ran out. So this change reaches the console by exactly one
# route, which is the read the script requires it to have made.
console_pause_sender harness || exit 1

RESTART_TOOK_MS=$(( ($(date +%s) - RESTART_CUE_AT) * 1000 ))
echo "[$(stamp)] == restart and pause took ${RESTART_TOOK_MS}ms, against a script that waits ${SCRIPT_WAIT_FOR_THE_RESTART_MS}ms for them =="

# Fatal rather than a warning, and the difference matters.
#
# The click below has already happened if the restart overran this wait, and a
# click that lands while the Host is away leaves the console holding the feed's
# Unreachable state, which has no retry of its own: the script would then fail
# ninety seconds later on an assertion about the feed, and that red would read
# as a console defect. Nothing about the console has been measured in that run,
# so it reports the thing that actually went wrong, which is this machine, and
# stops. There is no flaky assertion here to re-run past.
if (( RESTART_TOOK_MS >= SCRIPT_WAIT_FOR_THE_RESTART_MS )); then
    echo "The restart did not fit the script's second wait, so the Reconnect click landed" >&2
    echo "before the Host was up and this run says nothing about the console. That is the" >&2
    echo "machine, not the console: a restart on this box costs ~11s when it is quiet, and" >&2
    echo "this one took ${RESTART_TOOK_MS}ms. Re-run it, or raise the Wait in" >&2
    echo "console-long-outage-smoke.yaml and SCRIPT_WAIT_FOR_THE_RESTART_MS here together." >&2
    echo "Screenshots so far, including the drop and the still-down state, are in $CONSOLE_RESULTS." >&2
    exit 1
fi

echo "[$(stamp)] == waiting for the console to be reconnected by the operator's Reconnect, and to re-read =="
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
