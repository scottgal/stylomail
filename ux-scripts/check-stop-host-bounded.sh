#!/usr/bin/env bash
# Checks that console_stop_host cannot block, and that the key file goes first.
#
# A check rather than a smoke, and it lives here beside check-runner-gate.sh for
# the same reason: it needs no Host, no build, no console and no port, so there
# is no excuse for it not being run. It runs in seconds.
#
# The shape being reproduced is "a child that survives SIGTERM", which is what a
# process in uninterruptible kernel sleep looks like from the outside: the signal
# is delivered and not taken, the pid stays in the table, and a `wait` on it never
# returns. On 2026-10-01 that condition held about twenty `lsof` processes and
# one Host on this machine, and the unbounded `wait` it found here made a run
# that had already reached its verdict look like a run still at work for another
# eight minutes. A shell that traps and ignores SIGTERM gives the same shape
# without needing the kernel condition, which is the point of this file: the
# defect is testable without the thing that found it.
#
# What it does NOT check: that the bound is any particular number. It exports
# CONSOLE_HOST_STOP_WAIT itself, short, so the check takes seconds; the shipping
# default is 30 and is not what is under test.
#
# The scratch directory is this script's own and holds a placeholder rather than
# a key. Nothing here talks to a deployment, and nothing uses lsof.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

# Short so the check takes seconds rather than half a minute.
export CONSOLE_HOST_STOP_WAIT=5

# Its own run directory, so no real run's key file can be touched by this. Under
# the lane's scratch rather than /tmp, because an artifact in /tmp is not
# evidence and this one exists to be read after a failure.
export CONSOLE_RUN="${CONSOLE_RUN:-$REPO/.styloagent/scratch/desktop/stop-host-bounded-scratch}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

failures=0
case_number=0

pass() { echo "  ok   $1"; }
fail() { echo "  FAIL $1"; failures=$((failures + 1)); }

rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

elapsed() {
    local start="$1"
    echo $(( $(date +%s) - start ))
}

# Case 1: a child that stops when asked must stop quickly.
case_number=$((case_number + 1))
echo "case $case_number: a child that takes SIGTERM"

# The child's output goes to /dev/null, and that is not tidiness. A background
# child inherits this script's stdout, so an orphan left behind by a case holds
# the caller's pipe open after this script has exited, and whoever is reading it
# waits for an EOF that never comes. That is the same shape as the defect under
# test: something that has finished looking like something still working. It
# happened on the first run of this file, via the grandchild in case 2.
sleep 300 >/dev/null 2>&1 &
CONSOLE_HOST_PID=$!
start=$(date +%s)
console_stop_host
took=$(elapsed "$start")

if (( took < 3 )); then
    pass "returned in ${took}s, well inside the ${CONSOLE_HOST_STOP_WAIT}s bound"
else
    fail "took ${took}s for a child that stops on SIGTERM"
fi

if kill -0 "$CONSOLE_HOST_PID" 2>/dev/null; then
    fail "the child is still alive after a normal stop"
    kill -9 "$CONSOLE_HOST_PID" 2>/dev/null
else
    pass "the child is gone"
fi

# Case 2: a child that cannot take the signal must not block the caller, and
# must be named rather than waited on forever.
case_number=$((case_number + 1))
echo "case $case_number: a child that cannot take SIGTERM, with the key file present"

printf 'placeholder-not-a-key\n' > "$CONSOLE_RUN/auth.headers"

# The stand-in announces that its trap is installed before anything signals it,
# and that handshake is the difference between a check and a coin toss. `trap ""
# TERM` only takes effect once the child has run it, so a signal delivered in
# the first few milliseconds kills a child that was supposed to be unstoppable:
# this file passed and failed the same case on consecutive runs before the flag
# existed, because a `date` call happened to sit between the spawn and the kill.
# The `while` rather than a bare `sleep` is for the other half of the same
# problem: bash may replace itself with a single trailing command, and then the
# pid being signalled is the `sleep`, which dies at SIGTERM's default
# disposition and the case passes without testing anything.
ready="$CONSOLE_RUN/host-is-stubborn"
rm -f "$ready"

bash -c 'trap "" TERM; : > "$1"; while :; do sleep 1; done' _ "$ready" >/dev/null 2>&1 &
CONSOLE_HOST_PID=$!
child="$CONSOLE_HOST_PID"

for _ in {1..100}; do
    [[ -f "$ready" ]] && break
    sleep 0.1
done

if [[ ! -f "$ready" ]]; then
    fail "the stand-in never installed its trap, so this case cannot be read"
    kill -9 "$child" 2>/dev/null
    exit 1
fi

start=$(date +%s)
output="$(console_stop_host 2>&1)"
took=$(elapsed "$start")

if (( took >= CONSOLE_HOST_STOP_WAIT && took <= CONSOLE_HOST_STOP_WAIT + 5 )); then
    pass "returned after ${took}s, i.e. it gave up at the bound rather than waiting"
else
    fail "returned after ${took}s, which is not the bound of ${CONSOLE_HOST_STOP_WAIT}s"
fi

if [[ "$output" == *"$child"* ]]; then
    pass "the message names the pid"
else
    fail "the message does not name pid $child: $output"
fi

if [[ "$output" == *"uninterruptible"* ]]; then
    pass "the message says why, rather than reporting a run failure"
else
    fail "the message does not explain the condition: $output"
fi

if kill -0 "$child" 2>/dev/null; then
    pass "the child was left alone, as the message says"
    kill -9 "$child" 2>/dev/null
else
    fail "the child died, so nothing about the blocking case was tested"
fi

# The order claim: the key file goes even though the wait could not complete.
if [[ -f "$CONSOLE_RUN/auth.headers" ]]; then
    fail "the key file survived a stop that ran out of its wait"
else
    pass "the key file is gone even though the child could not be stopped"
fi

# Case 3: no Host at all still removes the key file and returns cleanly.
case_number=$((case_number + 1))
echo "case $case_number: no Host recorded"

printf 'placeholder-not-a-key\n' > "$CONSOLE_RUN/auth.headers"
unset CONSOLE_HOST_PID

start=$(date +%s)
console_stop_host
took=$(elapsed "$start")

if (( took < 3 )); then
    pass "returned in ${took}s"
else
    fail "took ${took}s with no pid to wait on"
fi

if [[ -f "$CONSOLE_RUN/auth.headers" ]]; then
    fail "the key file survived with no Host recorded"
else
    pass "the key file is gone"
fi

rm -rf "$CONSOLE_RUN"

echo
if (( failures == 0 )); then
    echo "stop-host-bounded: $case_number cases, 0 failures"
    exit 0
fi

echo "stop-host-bounded: $case_number cases, $failures failures"
exit 1
