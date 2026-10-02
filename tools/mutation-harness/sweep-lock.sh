#!/bin/sh
# Take the sweep lock from ANY mutation harness, including ad-hoc ones.
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
#     Any sweep harness, committed or ad-hoc, in .styloagent/tools/ or in /tmp, takes the lock
#     before its first mutation and removes it when it finishes.
#
# A bystander cannot audit for a tool they cannot see, so the lock is how a harness announces
# itself. `.styloagent/tools/mutate.py` isolates itself in a copy and does not need the lock to be
# safe, it takes the lock anyway, so that ONE signal covers every harness.
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
# Exits 1 if a sweep already holds the lock. Do not force it, two concurrent sweeps interleave
# mutations and attribute each other's failures.

set -eu

TOOLS=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
LOCK="$TOOLS/.mutation-sweep.lock"

# Captured HERE, before the `case` dispatches, because a function's own "$*" is empty when it is
# called with no arguments (`acquire) acquire ;;` passes none) and the lock would then record an
# empty command. Measured: it did exactly that on the first run of the verification.
INVOCATION="$0 $*"

usage() {
    awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$0"
    exit 2
}

acquire() {
    if [ -e "$LOCK" ]; then
        echo "!!! a sweep already holds the lock: $LOCK" >&2
        echo "    Read its first six lines: they name the holder, the PID and the UTC start" >&2
        echo "    time, which is what tells a running sweep from a leftover lock." >&2
        echo "    Wait for it to finish rather than forcing. Two concurrent sweeps interleave" >&2
        echo "    mutations and attribute each other's failures." >&2
        exit 1
    fi

    # WHO, AND SINCE WHEN. Added 2026-10-01. `ingress-` read a false red off this file's mere
    # PRESENCE and could not say who had held it, when, or whether the sweep was still alive, so
    # the one question a lane actually has ("was this caused by the sweep?") could not be answered
    # from the lock at all. A lock that cannot name its holder is a rumour that a concurrent sweep
    # existed, not a report that one did. STYLOAGENT_AGENT_PREFIX names the lane when the cockpit
    # exports it; the invoking command line usually carries the lane's scratch path when it does not.
    HOLDER=${STYLOAGENT_AGENT_PREFIX:-unknown}
    STARTED=$(date -u '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || echo unknown)
    WHERE=$(ps -o command= -p "$PPID" 2>/dev/null | head -1 | cut -c1-200 || true)
    [ -n "$WHERE" ] || WHERE=unknown

    {
        printf 'mutation sweep: RUNNING\n'
        printf 'holder:  %s    (set STYLOAGENT_AGENT_PREFIX to name the lane)\n' "$HOLDER"
        printf 'pid:     %s\n' "$$"
        printf 'started: %s (UTC)\n' "$STARTED"
        printf 'cmd:     %s\n' "$INVOCATION"
        printf 'invoked from: %s\n' "$WHERE"
        printf '\n'
        cat <<'LOCKFILE'
The lines above name the holder. Read them first: a lock with a stale start time and a PID that is
gone is a leftover, not a running sweep.

A mutation sweep is running RIGHT NOW, in an ISOLATED COPY of this tree under TMPDIR.
The shared tree is NOT being modified while this file exists.

The sweep copies the tree, applies each mutation to the COPY, builds and tests there, and deletes
the copy at the end. So a `dotnet test` you run in this repository is not reading mutated source,
and a red you see is about your code, not about the sweep.

It is still not nothing: the sweep builds and tests, so it competes for CPU, and a red on a
timing-sensitive test under load is worth re-taking. That is the only remaining reason to look at
this file before believing a failure.

WHAT A KILLED SWEEP LEAVES HAS MOVED. A SIGKILLed sweep cannot clean up, but it now leaves its work
inside its own copy, not in the shared tree: the applied mutation and the *.bak beside it are under
TMPDIR in `stylomail-sweep-*/repo`. So the surviving signal for that case is the directory:

    ls -d "${TMPDIR:-/tmp}"/stylomail-sweep-*   # a killed sweep's leftover working copy

A `*.bak` under `src/` is now a leftover from before this isolation landed, from a mutation applied
by hand, or from a lane's own tooling.

THE DIRECTION OF THIS FILE. The lock is written when a sweep starts and unlinked when it ends, so
its ABSENCE means the last sweep ended cleanly. SIGKILL cannot run that cleanup, so a killed sweep
LEAVES THE LOCK BEHIND: a lock present means a sweep is running now or was killed, and the header
above plus the leftover directory are what tell those two apart.
LOCKFILE
    } > "$LOCK"
}

release() {
    # WHO MAY UNLINK. Added 2026-10-02 by queue-. `release` was `rm -f "$LOCK"` with no check at
    # all, so ANY lane running it unlinked the lock of a sweep it did not hold: the one writer of
    # this path that never consulted the holder the header already names. A lock any non-holder can
    # unlink is a rumour that a sweep ended, not a report that one did.
    #
    # There is exactly ONE arm below that removes the lock, and it is the arm that positively proves
    # the holder is gone. Everything else refuses, including an unreadable header and a probe that
    # could not run, because reading an unusable instrument as "gone" turns a dead probe into a
    # release.
    if [ ! -e "$LOCK" ]; then
        echo "no lock at $LOCK; nothing to release"
        return 0
    fi

    # The holder's own pid, which `acquire` wrote as `$$` in this same shell. Tested FIRST because
    # `with` calls this from its EXIT trap while it IS the holder: without this arm the guard would
    # refuse to remove the lock its own process is holding, and `with` would leak it every run.
    held=$(sed -n 's/^pid:[[:space:]]*\([0-9][0-9]*\)[[:space:]]*$/\1/p' "$LOCK" | head -1)
    if [ -z "$held" ]; then
        echo "!!! refusing to release: $LOCK carries no readable 'pid:' line." >&2
        echo "    An unreadable holder counts as IN USE, not as gone. The lock reads:" >&2
        sed -n '1,6p' "$LOCK" >&2
        echo "    If you can see it is a leftover, remove it deliberately: rm -f $LOCK" >&2
        exit 1
    fi
    if [ "$held" = "$$" ]; then
        rm -f "$LOCK"
        return 0
    fi

    probe=$(ps -p "$held" -o pid= 2>/dev/null) && probe_rc=0 || probe_rc=$?
    if [ "$probe_rc" -eq 1 ] && [ -z "$probe" ]; then
        rm -f "$LOCK"
        return 0
    fi
    if [ "$probe_rc" -eq 0 ]; then
        echo "!!! refusing to release: the lock's holder pid $held is ALIVE." >&2
    else
        echo "!!! refusing to release: the liveness probe could not run (ps rc=$probe_rc)." >&2
    fi
    sed -n '1,6p' "$LOCK" >&2
    echo "    A live or unprobeable holder counts as IN USE. Wait for it to finish: two" >&2
    echo "    concurrent sweeps interleave mutations and attribute each other's failures." >&2
    exit 1
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
