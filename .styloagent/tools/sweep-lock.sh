#!/bin/sh
# Take the sweep lock from ANY mutation harness — including ad-hoc ones.
#
# WHY THIS EXISTS
# ---------------
# A mutation harness edits source files. Other lanes are running `dotnet test StyloMail.slnx` as
# their completion gate, and from their side a running sweep is indistinguishable from *their* code
# being broken. That has already happened once: `access-` measured Queue, saw failures that belonged
# to somebody else's tool, and recorded a doubt about correct work.
#
# `dotnet test StyloMail.slnx` mutates-in-place is the hazard; the harm lands on someone else.
#
# This is the convention, and it applies to harnesses nobody can audit for:
#
#     Any sweep harness — committed or ad-hoc, in .styloagent/tools/ or in /tmp — takes the lock
#     before its first mutation and removes it when it finishes.
#
# A bystander cannot audit for a tool they cannot see, so the lock is how a harness announces
# itself. `.styloagent/tools/mutate.py` isolates itself in a copy and does not need the lock to be
# safe — it takes the lock anyway, so that ONE signal covers every harness.
#
# USAGE
# -----
#   .styloagent/tools/sweep-lock.sh with ./my-mutation-round.sh     <- preferred
#   .styloagent/tools/sweep-lock.sh acquire                         <- then release yourself
#   .styloagent/tools/sweep-lock.sh release
#
# `with` acquires, runs your command, and releases on success, failure, Ctrl-C or SIGTERM. Prefer
# it: the failure mode of hand-rolled acquire/release is a harness that exits early and leaves the
# lock behind, which blocks every other sweep until somebody works out why.
#
# Exits 1 if a sweep already holds the lock. Do not force it — two concurrent sweeps interleave
# mutations and attribute each other's failures.

set -eu

TOOLS=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LOCK="$TOOLS/.mutation-sweep.lock"

usage() {
    sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
}

acquire() {
    if [ -e "$LOCK" ]; then
        echo "!!! a sweep already holds the lock: $LOCK" >&2
        echo "    Another mutation harness is running. Wait for it to finish rather than forcing," >&2
        echo "    and if it never finishes, read the lock file before removing it." >&2
        exit 1
    fi

    cat > "$LOCK" <<'LOCKFILE'
A mutation sweep is running RIGHT NOW and is editing source files in place.

While this file exists, `dotnet test` in this repository can fail for reasons that
have nothing to do with anyone's code: the sweep applies a mutation, builds, runs,
and restores. A concurrent test run sees the mutated source.

If you are running tests, wait for this file to disappear and re-run. If you are
seeing intermittent failures with no explanation, check whether this file exists
before concluding your suite is flaky.

*** THE LOCK IS ABSENT IN THE ONE CASE THAT MATTERS MOST. ***

SIGKILL cannot be handled, so a SIGKILLed sweep leaves mutated source, a .bak beside
it, and NO lock. So the check is BOTH signals:

    ls .styloagent/tools/.mutation-sweep.lock   # sweep running now
    find src -name '*.bak'                      # sweep was killed, mutation still applied

Either one means: do not trust a failure until the tree is confirmed clean.
LOCKFILE
}

release() {
    rm -f "$LOCK"
}

case "${1:-}" in
    with)
        shift
        [ $# -gt 0 ] || usage
        acquire
        trap release EXIT
        trap 'exit 130' INT
        trap 'exit 143' TERM
        "$@"
        ;;
    acquire) acquire ;;
    release) release ;;
    *) usage ;;
esac
