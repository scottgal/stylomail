#!/usr/bin/env bash
# Runs console-not-ready-smoke.yaml against a throwaway Host that is up and
# deliberately not ready, then takes it down again.
#
# The third state of the status line, and the only one where the console has
# something to list rather than something to say. "Connected, not accepting
# mail" is a Host that answers every request and will refuse the mail it cannot
# assess, so the console is reachable, every other pane works, and the reason
# has to be somewhere an operator can find it. That somewhere is the
# failed-checks block, which until now no run had ever put on screen.
#
# The state is reached by configuration, not by breaking anything: with no
# assessment credential at all the Host composes an assessor that refuses every
# message, and readiness says so. See CONSOLE_ASSESSOR in console-harness.sh.
#
# Run this, not the yaml directly, for the same reason as the other scripts:
# this one owns the Host the assertions are about and deletes it afterwards.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Set before the harness is sourced, because the harness reads it when it starts
# the Host.
#
# Two things, and neither is incidental. No assessment credential, which is what
# makes the Host not-ready. And no decision fixture, because a body the Host did
# not produce has no business on the screen of a Host that cannot produce one.
export CONSOLE_ASSESSOR=false
export CONSOLE_DECISION_FIXTURE=false

# This script's own scratch directory, set before the harness is sourced because
# the harness sets CONSOLE_RUN itself. A `${CONSOLE_RUN:-...}` default placed
# after the source keeps the harness's value rather than this one, and every
# script then shares the main smoke's directory.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-not-ready-ux}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_build_all || exit 1

# Its own output directory, for the reason the other scripts give: the main
# smoke wipes ux-results wholesale, so a run's artifacts must be somewhere one
# run owns. The scratch directory is set above, before the harness is sourced.
CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/not-ready"

rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, with no assessment credential =="
console_start_host || exit 1

# The ground truth the assertions are about, read before the console runs so a
# failure can be told apart: if the Host answered ready here, the console is not
# what went wrong.
echo
echo "== what the Host itself says =="
curl -sS -o "$CONSOLE_RUN/ready.json" -w "HTTP %{http_code}\n" "$CONSOLE_BASE/health/ready" || true
cat "$CONSOLE_RUN/ready.json" 2>/dev/null
echo

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-not-ready-smoke.yaml \
    --output "$CONSOLE_RESULTS"
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
