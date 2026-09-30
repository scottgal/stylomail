#!/usr/bin/env bash
# Checks the runner gate itself: console_final_status must return non-zero for a
# failing run and zero for a passing one.
#
# Why this exists. `4ee1084` made the six runners read their verdict from
# result.json instead of trusting `dotnet run`'s exit code, which is 0 for a
# script that failed. That fix is one function, and it is the only thing standing
# between a red console run and a green tick reported to the fleet, so it is
# worth a check of its own rather than being believed.
#
# Three shapes, and the shape that matters is the first:
#
#   1. A real failing artifact, kept at the path below. Real rather than
#      synthesized: it was produced by the harness on 2026-10-01 when the
#      quarantine run's first draft failed its 26th action, and it carries the
#      case that provoked the whole thing ("success": false with a non-zero
#      number of passing actions before it, which is exactly the shape a runner
#      would have read as green).
#   2. A missing file, because a run that never wrote a verdict is a failure
#      rather than an absence of one.
#   3. A passing artifact, which stops the gate passing this check by returning
#      non-zero for everything.
#
# Shape 3 needs a run to have happened: pass one as $1, or let it find the most
# recently written result.json under ux-results/. It refuses to call itself green
# when it could not check all three, because a check that silently skips a shape
# is the same defect in a smaller place.
#
# No Host, no console, no build: this reads files and calls one function.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-gatecheck}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

FAILING_ARTIFACT="$CONSOLE_REPO/.styloagent/scratch/desktop/quarantine-failed-result.json"

status=0

echo "== shape 1: a real failing artifact =="
if [[ ! -f "$FAILING_ARTIFACT" ]]; then
    echo "MISSING: $FAILING_ARTIFACT" >&2
    echo "That file is the preserved artifact from the first failing run this lane" >&2
    echo "ever had (the quarantine run's first draft, which failed on a Not.IsVisible" >&2
    echo "assertion whose locator matched no control). Without it this check cannot" >&2
    echo "run, and neither shape below can stand in for it: they are synthetic." >&2
    status=1
else
    console_final_status "$FAILING_ARTIFACT" 0
    gate=$?
    echo "returned $gate, expected non-zero"
    if [[ $gate -eq 0 ]]; then
        echo "FAIL: the gate returned 0 for a failing run." >&2
        status=1
    fi
fi

echo
echo "== shape 2: a result.json that does not exist =="
console_final_status "$CONSOLE_RUN/no-such-result.json" 0
gate=$?
echo "returned $gate, expected non-zero"
if [[ $gate -eq 0 ]]; then
    echo "FAIL: the gate returned 0 for a run with no verdict at all." >&2
    status=1
fi

echo
echo "== shape 3: a passing artifact =="
PASSING="${1:-}"
if [[ -z "$PASSING" ]]; then
    # Newest first, so a run that has just happened is the one checked. The main
    # smoke wipes ux-results wholesale, which is why this searches rather than
    # naming a path.
    PASSING="$(find "$CONSOLE_REPO/ux-results" -name result.json -print 2>/dev/null | head -1)"
fi

if [[ -z "$PASSING" || ! -f "$PASSING" ]]; then
    echo "NO PASSING ARTIFACT to check: run one of the runners first, or pass" >&2
    echo "its result.json as \$1. A gate that returned non-zero for everything" >&2
    echo "would pass the two shapes above." >&2
    status=1
else
    echo "using $PASSING"
    console_final_status "$PASSING" 0
    gate=$?
    echo "returned $gate, expected 0"
    if [[ $gate -ne 0 ]]; then
        echo "FAIL: the gate returned non-zero for a passing run." >&2
        status=1
    fi
fi

echo
if [[ $status -eq 0 ]]; then
    echo "gate ok: non-zero on a failing run and on a missing file, zero on a passing one"
else
    echo "gate NOT ok: see above"
fi

exit $status
