#!/usr/bin/env bash
# Checks the parts of the harness that must not be able to block a run: the stop
# path, the principal key's lifetime, and now the port preflight.
#
# A check rather than a smoke, and it lives here beside check-runner-gate.sh for
# the same reason: it needs no Host, no build, no console and no deployment, so
# there is no excuse for it not being run. It runs in seconds. It does bind one
# port of its own, an OS-assigned one, for the preflight case below.
#
# The file keeps its name although the subject has widened, because the README
# and a commit message both point at this path and a rename would break the one
# thing a reader follows.
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

# Its own run directory, under the lane's scratch rather than /tmp, because an
# artifact in /tmp is not evidence and this one exists to be read after a failure.
export CONSOLE_RUN="${CONSOLE_RUN:-$REPO/.styloagent/scratch/desktop/stop-host-bounded-scratch}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

# The sentence above used to read "so no real run's key file can be touched by
# this", and it was false: `${CONSOLE_RUN:-...}` keeps an INHERITED value, so a
# caller with CONSOLE_RUN exported handed this file its own run directory. This file
# then `rm -rf`s it and, worse, writes a placeholder OVER `auth.headers`, which is
# where a run keeps its principal key. `overview-` measured the same defect in the
# fingerprint check on 2026-10-01 and filed it HIGH; this one had the identical line
# and a sharper first move. The guard is shared so the two cannot drift apart.
if ! console_assert_run_dir_is_ours "$0" "deletes and then writes a placeholder key into"; then
    exit 2
fi

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

# Case 4: the port preflight answers "taken" for a listener, and "free" for the
# same port once the listener is gone. Both directions matter: a probe that
# always says free would let a run start against somebody else's Host, and one
# that always says taken would refuse every run.
case_number=$((case_number + 1))
echo "case $case_number: the port preflight, both directions"

port_file="$CONSOLE_RUN/probe-port"
rm -f "$port_file"

# An OS-assigned port rather than a fixed one, so this cannot collide with a real
# runner on 5290 or with another lane. The child holds it for the length of the
# case, and its output goes to /dev/null for the same reason as case 1's: a
# background child that inherits this script's stdout holds the caller's pipe
# open after the script has exited.
python3 - "$port_file" >/dev/null 2>&1 <<'PY' &
import socket, sys, time

listener = socket.socket()
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("127.0.0.1", 0))
listener.listen(1)

with open(sys.argv[1], "w") as handle:
    handle.write(str(listener.getsockname()[1]))
    handle.flush()

time.sleep(60)
PY
listener_pid=$!

for _ in {1..100}; do
    [[ -s "$port_file" ]] && break
    sleep 0.1
done

if [[ ! -s "$port_file" ]]; then
    # Refused rather than skipped, which is the rule this repo already applies to
    # the runner gate: a shape that could not be checked must not read as ok.
    fail "the listener never reported a port, so neither direction could be checked"
    kill -9 "$listener_pid" 2>/dev/null
    exit 1
fi

CONSOLE_PORT="$(cat "$port_file")"

# Three-valued, because the probe can also answer "I never asked". An `if` here
# would fold that third answer into whichever branch its else happens to be, and
# on the free direction below that branch is a PASS, which is how the defect this
# case exists to catch would have satisfied its own test.
console_port_is_taken
case $? in
    0) pass "a live listener on $CONSOLE_PORT reads as taken" ;;
    1) fail "a live listener on $CONSOLE_PORT read as free, so a run would start against it" ;;
    *) fail "the probe could not run, so $CONSOLE_PORT was never asked about" ;;
esac

# The descriptor must not survive the probe. If it did, the harness would hold a
# connection to the listener it had just refused to run against, which is a real
# bug rather than a tidiness point: the subshell in console_port_is_taken is what
# prevents it, and this is what would catch its removal.
if { true >&3; } 2>/dev/null; then
    fail "the probe left fd 3 open in the caller"
else
    pass "the probe left no descriptor behind"
fi

kill -9 "$listener_pid" 2>/dev/null
wait "$listener_pid" 2>/dev/null

# The port is free now, and the probe has to say so rather than caching the
# previous answer.
console_port_is_taken
case $? in
    0) fail "the port read as taken after the listener was killed" ;;
    1) pass "the same port reads as free once the listener is gone" ;;
    *) fail "the probe could not run, so the free direction was never checked" ;;
esac

# The port is an argument now, because run-console-address-change-smoke.sh has to
# ask about the address it SAVES rather than the port this run binds. Three
# assertions, each falsifiable in a different direction, and the state above is
# what makes them measurable: the port is free here, so a call that wrongly fell
# through to it would answer free and be caught rather than agreeing by accident.
#
#   - the same free port asked explicitly must answer what the default answered,
#     or a runner's precondition would be a different question from the harness's;
#   - a port that is not a number must be 2, not 1, because a connect to it fails
#     for a reason that is not "nothing is listening", and reading that as free is
#     the fail-open this file refuses everywhere else;
#   - an EXPLICIT EMPTY argument must also be 2. That is the reason the default is
#     not spelled `${1:-$CONSOLE_PORT}`: with the colon an empty argument falls
#     through to CONSOLE_PORT and probes the run's own port, which is free here.
console_port_is_taken "$CONSOLE_PORT"
explicit_status=$?
if (( explicit_status == 1 )); then
    pass "asking about $CONSOLE_PORT explicitly gives the answer the default gave"
else
    fail "the explicit-argument call answered $explicit_status where the default answered free"
fi

console_port_is_taken "notaport"
notaport_status=$?
if (( notaport_status == 2 )); then
    pass "a port that is not a number answers cannot-tell rather than free"
else
    fail "a port that is not a number answered $notaport_status, and 1 would start a run on it"
fi

console_port_is_taken ""
empty_status=$?
if (( empty_status == 2 )); then
    pass "an explicit empty argument answers cannot-tell rather than falling through to CONSOLE_PORT"
else
    fail "an empty argument answered $empty_status, so it fell through to the run's own port"
fi

# Case 5: the guard. The README says the harness uses no lsof, and it said that
# while console_start_host called lsof twice, so the claim needs a test rather
# than a sentence. Comment lines are stripped first: every lsof mention left in
# the harness is prose explaining why it is gone, and the guard is about
# invocations.
#
# The detection is a function so that the guard and the control below run the
# same code. Two copies of a grep is how a control stops controlling anything.
count_lsof_calls() {
    grep -v '^[[:space:]]*#' "$1" | grep -c 'lsof' || true
}

case_number=$((case_number + 1))
echo "case $case_number: neither the harness nor a runner invokes lsof"

# What this does not catch, said plainly because a guard read as wider than it is
# is how the claim drifted the first time: an lsof reached through a variable or a
# built command would pass. It catches the direct call, which is the form the
# defect took.
code_lsof="$(count_lsof_calls "$HERE/console-harness.sh")"

if (( code_lsof == 0 )); then
    pass "no lsof call in console-harness.sh outside a comment"
else
    fail "console-harness.sh calls lsof on $code_lsof line(s) outside a comment"
fi

# THE POPULATION WAS ONE FILE, AND THE DEFECT WAS IN ANOTHER ONE. This case used
# to read console-harness.sh and nothing else, while `run-console-address-change-
# smoke.sh` sat one directory away with a live lsof preflight on the same run
# path: measured on 2026-10-01, the harness at 0 and that runner at 1. The claim
# being tested is about the run path, so the sweep covers the population the claim
# is made over: every `run-console-*.sh` and the probe script beside them, the
# same eleven files `check-run-dir-refusal.sh` counts.
#
# The synthetic control below shows the DETECTOR can fire; this sweep is the one
# that has to show the population is the population. A glob that stops matching,
# or a rename that walks the offending file out of it, would leave the control
# green and the claim unchecked, so the loop records whether it reached the file
# this paragraph names and fails if it did not.
saw_address_change=0
for f in "$HERE"/run-console-*.sh "$HERE"/probe-submission-route.sh; do
    [[ -f "$f" ]] || continue
    [[ "$(basename "$f")" == "run-console-address-change-smoke.sh" ]] && saw_address_change=1
    runner_lsof="$(count_lsof_calls "$f")"
    if (( runner_lsof == 0 )); then
        pass "no lsof call in $(basename "$f") outside a comment"
    else
        fail "$(basename "$f") calls lsof on $runner_lsof line(s) outside a comment"
    fi
done

if (( saw_address_change )); then
    pass "the sweep reached run-console-address-change-smoke.sh, the file whose lsof call it was written for"
else
    fail "the sweep never reached run-console-address-change-smoke.sh, so a green above would not cover it"
fi

# The positive control, and without one the line above proves nothing. An absence
# assertion passes just as happily when its pattern has stopped matching anything
# as when the thing is genuinely absent, so the guard has to show its detector can
# see an lsof call somewhere. `ingress-` broadcast this trap to the fleet on 1 Oct
# and the shape is the same one: a literal that no longer matches the source makes
# a DoesNotContain pass by matching nothing. DecisionViewTests'
# Coverage_names_only_what_was_true is the same construction and pairs its
# DoesNotContain with two Contains for this reason.
control="$CONSOLE_RUN/lsof-control.sh"
printf '#!/usr/bin/env bash\nlsof -nP -iTCP:"$PORT" -sTCP:LISTEN\n' > "$control"
control_hits="$(count_lsof_calls "$control")"
rm -f "$control"

if (( control_hits >= 1 )); then
    pass "the same detection finds an lsof call in a file that has one"
else
    fail "the detection found no lsof in a file containing one, so the guard above proves nothing"
fi

# Case 6: the caller, not just the probe. A taken port must stop the run before
# anything is started, with a message that names the port and says how to find
# what holds it. This returns before the binary check inside console_start_host,
# so it needs no build, and that ordering is itself part of what is asserted:
# reaching for the binary first would mean a run with no Host built failed for
# the wrong reason.
case_number=$((case_number + 1))
echo "case $case_number: a taken port stops the run before anything starts"

port_file="$CONSOLE_RUN/probe-port-2"
rm -f "$port_file"

python3 - "$port_file" >/dev/null 2>&1 <<'PY' &
import socket, sys, time

listener = socket.socket()
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("127.0.0.1", 0))
listener.listen(1)

with open(sys.argv[1], "w") as handle:
    handle.write(str(listener.getsockname()[1]))
    handle.flush()

time.sleep(60)
PY
listener_pid=$!

for _ in {1..100}; do
    [[ -s "$port_file" ]] && break
    sleep 0.1
done

if [[ ! -s "$port_file" ]]; then
    fail "the listener never reported a port, so the refusal could not be checked"
    kill -9 "$listener_pid" 2>/dev/null
    exit 1
fi

CONSOLE_PORT="$(cat "$port_file")"
start=$(date +%s)
output="$(console_start_host 2>&1)"
took=$(elapsed "$start")

kill -9 "$listener_pid" 2>/dev/null
wait "$listener_pid" 2>/dev/null

if (( took < 3 )); then
    pass "refused in ${took}s rather than probing indefinitely"
else
    fail "took ${took}s to refuse a port that was already listening"
fi

if [[ "$output" == *"$CONSOLE_PORT"* ]]; then
    pass "the message names the port"
else
    fail "the message does not name port $CONSOLE_PORT: $output"
fi

if [[ "$output" == *"netstat"* ]]; then
    pass "the message says how to find what holds it, without lsof"
else
    fail "the message does not say how to find the holder: $output"
fi

# Case 7: the guard. This file carried the same `${CONSOLE_RUN:-...}` line as the
# fingerprint check, and a sharper first move: case 2 above writes a placeholder
# OVER `$CONSOLE_RUN/auth.headers`, which is where a run keeps its principal key.
# So the refusal is asserted HERE and not merely assumed to follow from the shared
# function, because what has to be true is that this file calls it. Mutation F in
# the falsification removes that call and this case is what goes red.
if [[ -n "${CONSOLE_STOP_CHILD:-}" ]]; then
    echo "case 7: skipped in the child case 7 started, so the guard runs exactly once"
else
case_number=$((case_number + 1))
echo "case $case_number: a caller's run directory is refused, not adopted and overwritten"

decoy="$REPO/.styloagent/scratch/desktop-decoy-for-stop-case"
rm -rf "$decoy"
mkdir -p "$decoy"
printf 'a-callers-principal-key\n' > "$decoy/auth.headers"

output="$(env CONSOLE_RUN="$decoy" CONSOLE_STOP_CHILD=1 bash "$HERE/check-stop-host-bounded.sh" 2>&1)"
status=$?

if (( status == 2 )); then
    pass "refused with exit 2 rather than running"
else
    fail "ran anyway with an inherited CONSOLE_RUN, exit $status"
fi

if [[ "$output" == *"refusing to run"* ]]; then
    pass "the refusal says so on stderr rather than failing an assertion"
else
    # The nested run's output is a whole check's output, and it carries its own
    # "case N:" headers. Embedded raw at column 0 they are indistinguishable from
    # THIS file's headers to anything parsing this file's output: the falsifier reads
    # "case N:" to attribute a later FAIL, and when a mutation let the child run it
    # attributed this case's third failure to the child's case number, which moved
    # when a case was added here. Indenting from the second line keeps the message
    # readable and the child's headers out of the parser's reach.
    fail "refused without saying why: $(printf '%s\n' "$output" | sed -e '2,$s/^/    /')"
fi

# The value, not just the file's existence: this file's destructive move is an
# overwrite, so a case that only checked for a file would pass on a wrecked one.
if [[ -f "$decoy/auth.headers" && "$(<"$decoy/auth.headers")" == "a-callers-principal-key" ]]; then
    pass "the caller's key file is intact, which is the whole point"
else
    fail "the caller's key file was overwritten or deleted"
fi

# The control, and it calls the guard directly for the reason the fingerprint
# check's control does: a child running this whole file would inherit every other
# case's verdict and make this case red for reasons that are not the guard.
ours="$REPO/.styloagent/scratch/desktop/stop-case-ours"
rm -rf "$ours"
env CONSOLE_RUN="$ours" bash -c 'source "$1"; console_assert_run_dir_is_ours "$2"' _ "$HERE/console-harness.sh" "$0" >/dev/null 2>&1
status=$?

if (( status == 0 )); then
    pass "the same guard lets a directory under the lane's scratch through"
else
    fail "a directory under the lane's own scratch was refused too, exit $status"
fi

rm -rf "$decoy" "$ours"
fi

# Case 8: a port that cannot be probed is refused, not read as free. `nimble-`
# broadcast this shape against their own netstat gate on 2026-10-01: a probe that
# dies produces the same bytes as a quiet endpoint, so the gate says clear and the
# run starts. This file's probe is a direct connect and does not have their bug, but
# an empty CONSOLE_PORT makes that connect fail for a reason that is not "nothing is
# listening". The refusal has to name the real problem: "already in use" would send
# the reader looking for a holder that does not exist.
case_number=$((case_number + 1))
echo "case $case_number: a CONSOLE_PORT that is not a number is refused, not read as free"

saved_port="$CONSOLE_PORT"
CONSOLE_PORT=""
output="$(console_start_host 2>&1)"
status=$?
CONSOLE_PORT="not-a-port"
output_bad="$(console_start_host 2>&1)"
status_bad=$?
CONSOLE_PORT="$saved_port"

if (( status != 0 && status_bad != 0 )); then
    pass "an empty and a non-numeric port are both refused"
else
    fail "an unprobeable port was accepted (empty exit $status, non-numeric exit $status_bad)"
fi

if [[ "$output" == *"not a port number"* && "$output_bad" == *"not a port number"* ]]; then
    pass "the refusal names the port as the problem"
else
    fail "the refusal does not say the port is the problem: $output"
fi

if [[ "$output" != *"already in use"* ]]; then
    pass "it does not claim a holder it never found"
else
    fail "it reports a holder for a port it could not probe: $output"
fi

# Case 9: the third value is REFUSED, not read either way. Cases above check the two
# answers a working probe gives; this one checks the answer of a probe that cannot
# run at all, which is the state this harness spent a night reading as "free".
#
# The state is a process with no descriptor left, which is reachable, was measured
# while the defect was being confirmed (122877 held descriptors, see the probe's own
# comment), and which this case can create locally. Only THIS SUBSHELL's table is
# filled: the machine's is not touched, so the case is safe to run beside other
# lanes, and the system-wide condition stays unmeasured because filling the real
# table would take every lane down.
#
# The third assertion is what makes the first two mean anything: a CONTROL in the
# unsqueezed parent must get an ordinary answer for the SAME port, or the case is
# asserting something about the machine rather than about the code.
case_number=$((case_number + 1))
echo "case $case_number: a probe that cannot run is refused, not read either way"

scratch_dir="$REPO/.styloagent/scratch/desktop"
mkdir -p "$scratch_dir"
capture="$scratch_dir/port-probe-refusal.out"

# Numeric and free: the listener the earlier cases killed is what left this port in
# CONSOLE_PORT. Without a real number the third value would never be reached, so the
# case fails rather than reporting on a state it did not create.
if [[ ! "$CONSOLE_PORT" =~ ^[0-9]+$ ]]; then
    fail "the case could not set up: CONSOLE_PORT is '$CONSOLE_PORT', not a number"
else
    (
        # Opened before the squeeze. After it no new descriptor can be had, so a
        # `$( )` capture would need a pipe and would be measuring this script.
        exec 9>"$capture"

        # A small table, filled completely. Descriptors 0, 1, 2 and 9 are open, so
        # the count is computed rather than probed for the edge: a failed `exec`
        # redirection takes a non-interactive shell down with it, which would kill
        # this subshell silently instead of leaving a red case behind.
        ulimit -n 64
        lim="$(ulimit -n)"
        echo "limit=$lim" >&9
        i=3
        opened=0
        while (( opened < lim - 4 )); do
            if (( i != 9 )); then
                eval "exec $i<>/dev/null" || break
                opened=$((opened + 1))
            fi
            i=$((i + 1))
        done

        # The squeeze is asserted, not assumed: if the table still has room, the
        # probe below is answering about a working machine and the case is vacuous.
        if ! : < /dev/null 2>/dev/null; then
            echo "squeezed=yes" >&9
        else
            echo "squeezed=no" >&9
        fi

        console_port_is_taken
        echo "probe=$?" >&9

        # A path that cannot exist, so that a probe which wrongly says "free" stops
        # at the binary check rather than starting a Host inside this squeeze. It is
        # set in the subshell only, and it does not change what is under test: the
        # state is the probe's answer, and everything after it is not this case.
        CONSOLE_HOST_APP="$scratch_dir/no-binary-under-test"
        console_start_host >&9 2>&9
        echo "start=$?" >&9
    )

    squeezed="$(sed -n 's/^squeezed=//p' "$capture")"
    probe_status="$(sed -n 's/^probe=//p' "$capture")"
    start_status="$(sed -n 's/^start=//p' "$capture")"
    captured="$(cat "$capture")"

    if [[ "$squeezed" != yes ]]; then
        fail "the descriptor squeeze did not take, so the third value was never exercised: $captured"
    elif [[ "$probe_status" != 2 ]]; then
        fail "a probe with no descriptor left answered '$probe_status' rather than the third value"
    else
        pass "a probe that cannot run reports the third value, not free"
    fi

    if [[ -n "$start_status" && "$start_status" != 0 ]]; then
        pass "console_start_host refuses when the port cannot be probed"
    else
        fail "console_start_host did not refuse a port it never asked about (exit '$start_status')"
    fi

    if [[ "$captured" != *"already in use"* ]]; then
        pass "the refusal does not claim a holder it never found"
    else
        fail "it reports a holder for a port it could not probe"
    fi

    console_port_is_taken
    control=$?
    if (( control != 2 )); then
        pass "the same port answers normally when descriptors are available (exit $control)"
    else
        fail "the port answers cannot-tell with descriptors free, so the squeeze proves nothing"
    fi

    rm -f "$capture"
fi

rm -rf "$CONSOLE_RUN"

echo
if (( failures == 0 )); then
    echo "stop-host-bounded: $case_number cases, 0 failures"
    exit 0
fi

echo "stop-host-bounded: $case_number cases, $failures failures"
exit 1
