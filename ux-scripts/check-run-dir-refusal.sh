#!/usr/bin/env bash
# Checks the rule that decides a RUNNER's run directory: whose directory it is,
# whether an inherited one may be adopted, and who clears it.
#
# Why this exists. Eleven runners and the main smoke carried
#
#     export CONSOLE_RUN="${CONSOLE_RUN:-<own /tmp path>}"
#     ...
#     rm -rf "$CONSOLE_RUN"
#
# `${VAR:-x}` substitutes only when the variable is EMPTY, so a caller with
# CONSOLE_RUN exported handed a runner their own directory to delete: `export
# CONSOLE_RUN=/tmp/run-a` and then any of the twelve deleted run A, principal key
# and artifacts together. Measured 2026-10-01, filed medium, ruled by `overview-`
# as: each runner keeps its own default, and an inherited value is REFUSED unless
# the caller sets CONSOLE_REUSE_RUN=1. The clearing moved into one function,
# console_runner_run_dir, so the destructive half exists once.
#
# No Host and no build: everything here is a function call, a child process that
# refuses in its first second, or a file read. Only the first case runs a runner,
# and it stops at the refusal. The adopt-and-clear direction is exercised THROUGH
# THE FUNCTION rather than through a whole run, and that limit is stated rather
# than papered over: a whole run builds the solution, and a check that builds is
# not a check anyone runs.
#
# Nothing here touches a runner's real default under /tmp. The behavioural cases
# use paths under this check's own scratch, because a check that clears another
# script's run directory would be an instance of the defect it is testing for.
#
# The population is enumerated rather than recited, and the count is asserted, so
# a runner that is added, renamed away or quietly dropped shows up here as a red
# instead of as a smaller sweep.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

# Its own run directory, under the lane's scratch rather than /tmp, for the reason
# the sibling check gives: an artifact in /tmp is not evidence, and this one exists
# to be read after a failure. It holds no key: the "principal key" below is a
# sentinel string in a file this check made.
export CONSOLE_RUN="${CONSOLE_RUN:-$REPO/.styloagent/scratch/desktop/run-dir-refusal-scratch}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

if ! console_assert_run_dir_is_ours "$0" "clears"; then
    exit 2
fi

failures=0
case_number=0

pass() { echo "  ok   $1"; }
fail() { echo "  FAIL $1"; failures=$((failures + 1)); }

rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

# The eleven, measured by glob so a rename cannot hide, and asserted so a shorter
# glob cannot pass as a smaller population. run-console-smoke.sh is one of them:
# it is the script with no default of its own, which is why it used to wipe
# whatever the harness had set.
RUNNERS=()
for f in "$HERE"/run-console-*.sh "$HERE"/probe-submission-route.sh; do
    [[ -f "$f" ]] && RUNNERS+=("$f")
done
EXPECTED_RUNNERS=11

# Reads a runner's own declared default out of the file, so the assertion in the
# last case is about what the file says rather than about what this check
# remembers having written. Empty for a runner that names none, which is the main
# smoke: it passes the harness's main path instead.
declared_default() {
    sed -n 's/^CONSOLE_RUN_DEFAULT="\(.*\)"$/\1/p' "$1" | head -1
}

# Case 1: a real runner refuses an inherited CONSOLE_RUN, and the directory it was
# pointed at is still there afterwards. This is the defect itself, on the real
# script rather than on a stand-in, and the sentinels stand in for the principal
# key and the artifacts the wipe used to take with it.
case_number=$((case_number + 1))
echo "case $case_number: a runner refuses an inherited CONSOLE_RUN and leaves it alone"

victim="$CONSOLE_RUN/victim"
rm -rf "$victim"
mkdir -p "$victim"
printf 'sentinel-not-a-real-key\n' > "$victim/principal.key"
printf 'sentinel-artifact\n' > "$victim/host.db"

# The check must not be able to start a build, even in the case it exists to
# catch. With the refusal gone the runner carries straight on into
# console_build_all, and a check that can start a solution build is a check nobody
# runs while the gate is shut. So `dotnet` is stubbed for the child alone, and the
# stub records that it was called: "nothing was built" is then measured by whether
# the build tool ran, rather than by the harness's banner line, which is echoed
# before the build on both paths and so cannot tell them apart.
export DOTNET_STUB_MARKER="$CONSOLE_RUN/dotnet-was-called"
rm -f "$DOTNET_STUB_MARKER"
dotnet() { printf 'called\n' >> "${DOTNET_STUB_MARKER:-/dev/null}"; return 127; }
export -f dotnet

output="$(env CONSOLE_RUN="$victim" \
    CONSOLE_BUILD_LOG_DIR="$CONSOLE_RUN/child-build-log" \
    bash "$HERE/run-console-no-feed-smoke.sh" 2>&1)"
status=$?

if [[ $status -eq 2 ]]; then
    pass "exit 2, which is the refusal"
else
    fail "expected exit 2 from the refusal, got $status"
fi

if [[ -f "$victim/principal.key" && -f "$victim/host.db" ]]; then
    pass "the inherited directory still holds both sentinels"
else
    fail "the inherited directory was cleared by a run that does not own it"
fi

if [[ "$output" == *CONSOLE_REUSE_RUN* ]]; then
    pass "the refusal names the opt-in that would allow it"
else
    # Flattened onto the FAIL line rather than embedded with its own newlines: the
    # falsifier reads the case it belongs to from the captured text, and a child's
    # second line would start at column 0 where a `case N:` header is expected.
    fail "the refusal does not say how to proceed deliberately: $(printf '%s' "$output" | head -3 | tr '\n' ' ')"
fi

# The refusal has to come before the build, not after it. console_build_all stamps
# into $CONSOLE_RUN, so a refusal after it would already have written a manifest
# naming this build into another run's directory. The listing is the measurement:
# the two sentinels and nothing else is what "nothing was written here" looks like.
if [[ -e "$DOTNET_STUB_MARKER" ]]; then
    fail "the child reached the build tool, so the refusal came after the build or not at all"
else
    pass "the build tool was never called"
fi

held="$(cd "$victim" && printf '%s\n' * | sort | tr '\n' ' ')"
if [[ "$held" == "host.db principal.key " ]]; then
    pass "and the inherited directory holds only its two sentinels"
else
    fail "the inherited directory now holds: ${held:-<empty>}"
fi

# Case 2: with nothing inherited, the function takes the default it is given and
# clears it. The sentinel is what makes the clearing measurable: a directory that
# is empty because it was just created says nothing about the wipe. The default
# here is this check's own path rather than a runner's, so no live /tmp run is at
# risk from a check about other people's run directories.
case_number=$((case_number + 1))
echo "case $case_number: an uninherited call takes the given default and clears it"

given="$CONSOLE_RUN/given-default"
rm -rf "$given"
mkdir -p "$given"
printf 'sentinel-from-a-previous-run\n' > "$given/previous-run-artifact"

got="$(env -u CONSOLE_RUN bash -c '
    source "$1/console-harness.sh" >/dev/null 2>&1
    console_runner_run_dir "$2" >/dev/null 2>&1 || exit 9
    printf "%s" "$CONSOLE_RUN"
' _ "$HERE" "$given")"
rc=$?

if [[ "$got" == "$given" ]]; then
    pass "the run directory is the one it was given"
else
    fail "expected $given, got ${got:-<empty>} (rc $rc)"
fi

if [[ -e "$given/previous-run-artifact" ]]; then
    fail "the previous run's artifact survived, so the directory was not cleared"
else
    pass "the previous run's artifact is gone"
fi

# Case 3: the opt-in adopts the inherited directory and clears it. The subject
# MOVING is the point: the same call, the same two directories, one variable
# different from case 4 below.
case_number=$((case_number + 1))
echo "case $case_number: CONSOLE_REUSE_RUN=1 adopts the inherited directory, and clears it"

adopted="$CONSOLE_RUN/adopted"
rm -rf "$adopted"
mkdir -p "$adopted"
printf 'sentinel-that-must-go\n' > "$adopted/old-run-artifact"

got="$(env CONSOLE_RUN="$adopted" CONSOLE_REUSE_RUN=1 bash -c '
    source "$1/console-harness.sh" >/dev/null 2>&1
    console_runner_run_dir "$2" >/dev/null 2>&1 || exit 9
    printf "%s" "$CONSOLE_RUN"
' _ "$HERE" "$CONSOLE_RUN/not-the-adopted-one")"
rc=$?

if [[ "$got" == "$adopted" ]]; then
    pass "CONSOLE_RUN is the inherited directory"
else
    fail "expected CONSOLE_RUN=$adopted, got ${got:-<empty>} (rc $rc)"
fi

if [[ -e "$adopted/old-run-artifact" ]]; then
    fail "the opt-in adopted the directory but did not clear it"
else
    pass "the adopted directory was cleared, which is what the opt-in agrees to"
fi

# Case 4: the control for case 3, and it is the same call with the opt-in removed.
# Without it the subject must not move, which is what makes case 3 evidence that
# the VARIABLE is the mechanism rather than that this call adopts anything.
case_number=$((case_number + 1))
echo "case $case_number: the same call without the opt-in refuses (the control for case 3)"

control="$CONSOLE_RUN/control"
rm -rf "$control"
mkdir -p "$control"
printf 'sentinel-that-must-stay\n' > "$control/old-run-artifact"

env -u CONSOLE_REUSE_RUN CONSOLE_RUN="$control" bash -c '
    source "$1/console-harness.sh" >/dev/null 2>&1
    console_runner_run_dir "$2" >/dev/null 2>&1
    exit $?
' _ "$HERE" "$CONSOLE_RUN/not-the-control-one" >/dev/null 2>&1
rc=$?

if [[ $rc -eq 2 ]]; then
    pass "refused with 2, the caller-must-not-proceed status"
else
    fail "expected the refusal status 2, got $rc"
fi

if [[ -e "$control/old-run-artifact" ]]; then
    pass "and the directory it was pointed at is untouched"
else
    fail "the control directory was cleared without the opt-in"
fi

# Case 5: the eleven are wired, and none of them still clears the run directory
# itself. A function nothing calls is not a fix, and the twelve copies of `rm -rf`
# are what this change exists to remove, so both halves are asserted per file. The
# last clause is the one that keeps the new rule from being undone at a single
# call site: each runner names its own directory, so an inherited one is never
# silently the equal of the default and never adopted by accident.
case_number=$((case_number + 1))
echo "case $case_number: all $EXPECTED_RUNNERS runners are wired and none clears \$CONSOLE_RUN itself"

if [[ ${#RUNNERS[@]} -eq $EXPECTED_RUNNERS ]]; then
    pass "found ${#RUNNERS[@]} runners"
else
    fail "found ${#RUNNERS[@]} runners, expected $EXPECTED_RUNNERS"
fi

mains=0
for f in "${RUNNERS[@]}"; do
    name="$(basename "$f")"
    # Anchored at column 0, which is where both are called. Unanchored, the first
    # match for console_build_all is the comment two lines above it, and the check
    # then reddens nine runners over a line of prose: measured, not hypothetical,
    # it is what the first run of this case reported.
    call_line="$(/usr/bin/grep -n '^console_runner_run_dir "' "$f" | head -1 | cut -d: -f1)"
    build_line="$(/usr/bin/grep -n '^console_build_all' "$f" | head -1 | cut -d: -f1)"
    if [[ -z "$call_line" ]]; then
        fail "$name never calls console_runner_run_dir"
        continue
    fi
    if [[ -z "$build_line" ]]; then
        fail "$name calls it but never builds, so the order is unmeasurable"
        continue
    fi
    if (( call_line >= build_line )); then
        fail "$name calls it at line $call_line, at or after its build at $build_line"
        continue
    fi
    if /usr/bin/grep -q 'rm -rf "\$CONSOLE_RUN"' "$f"; then
        fail "$name still clears \$CONSOLE_RUN itself, so there are two rules again"
        continue
    fi

    own="$(declared_default "$f")"
    if [[ -n "$own" ]]; then
        if [[ "$own" == "$CONSOLE_RUN_DEFAULT_MAIN" ]]; then
            fail "$name declares the main smoke's directory as its own: $own"
            continue
        fi
        pass "$name wired at line $call_line, own default $own"
    else
        # No default of its own: the only runner allowed to be that is the main
        # smoke, and it must be passing the harness's main path explicitly.
        if /usr/bin/grep -q 'console_runner_run_dir "\$CONSOLE_RUN_DEFAULT_MAIN"' "$f"; then
            mains=$((mains + 1))
            pass "$name wired at line $call_line, on the harness's main default"
        else
            fail "$name declares no default of its own and does not name the main one either"
        fi
    fi
done

if [[ $mains -eq 1 ]]; then
    pass "exactly one runner runs in $CONSOLE_RUN_DEFAULT_MAIN"
else
    fail "$mains runners run in $CONSOLE_RUN_DEFAULT_MAIN, expected exactly 1"
fi

echo
if [[ $failures -eq 0 ]]; then
    echo "run-directory rule ok: ${#RUNNERS[@]} runners wired to one refusing function, no runner clearing its own \$CONSOLE_RUN"
    exit 0
fi
echo "run-directory rule NOT ok: $failures failure(s) above"
exit 1
