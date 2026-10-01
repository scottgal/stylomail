#!/usr/bin/env bash
# Runs console-smoke.yaml against a throwaway Host, then takes it down again.
#
# Run this, not the yaml directly. The script owns the Host the assertions are
# about, and deletes it afterwards, so the run is repeatable from any starting
# state and leaves nothing behind.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

# Both binaries, from a clean checkout, with the output shown. This is the only
# build step: the Host is built here rather than launched with `dotnet run` so
# the pid the harness records is the Host itself and the cleanup can stop it.
console_build_all || exit 1

rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

# This runner's own output directory, and the wipe below is scoped to it.
#
# The wipe used to be `rm -rf "$CONSOLE_REPO/ux-results"`, the whole tree, while
# the eight sibling runners each declared a subdirectory of it "because the main
# smoke wipes ux-results wholesale". A subdirectory of a directory that is
# `rm -rf`'d does not survive the wipe, so that mitigation did not mitigate:
# every one of those runners wrote its evidence into a place the next main smoke
# deleted. It was not hypothetical. The long-outage run's five screenshots were
# gone by the time `overview-` looked for them, destroyed by a main smoke twenty
# minutes later, and ux-results is gitignored (.gitignore:137) so nothing was
# recoverable. Found at source by `overview-` while correcting a document that
# cited those screenshots.
#
# The directory is still cleared rather than appended to, which is the original
# reason and still true: the harness writes screenshots without removing what a
# previous run left, so a step that stops running leaves its old image behind and
# it reads as this run's output. That cost real time as well: a screenshot from a
# passing run was read as evidence about a failing one.
CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-smoke"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

# Something to group, so the sidebar's grouping is asserted rather than
# invisible behind a single ungrouped row.
console_seed_management

# And a real decision for the console to render. An assessment-only call is
# recorded in the ledger, so this is evidence the Host produced rather than a
# fixture the console wrote for itself.
#
# It works because the provider is unreachable, which the harness sets before
# starting the Host: policy then declines and records a decision whose semantic
# dimensions are all Unavailable. That is the state the pane must render as
# absent rather than as a number, and it is the only way to reach it without
# the operator's provider key.
console_seed_decision "$CONSOLE_BASE"

echo "== driving the console =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-smoke.yaml \
    --output "$CONSOLE_RESULTS"
STATUS=$?

echo
echo "== result =="
# The name the harness writes, not the one this used to look for. It read
# `console-smoke.json`, which nothing has ever written, so every passing run
# printed an empty result block and the numbers behind the pass were only in
# the file. A runner that cannot show its own evidence is one step from a
# runner that does not have any.
if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
    cat "$CONSOLE_RESULTS/result.json"
fi
echo
echo "screenshots in $CONSOLE_RESULTS/"

# The harness's exit code is not its verdict: a failing script exits 0. Read the
# verdict from result.json before reporting anything. This is the runner the
# fleet's completion gate calls, so a green tick here that came from an exit code
# rather than from the assertions is the one failure mode that matters most.
console_final_status "$CONSOLE_RESULTS/result.json" "$STATUS"
exit $?
