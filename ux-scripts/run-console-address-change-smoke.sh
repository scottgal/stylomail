#!/usr/bin/env bash
# Runs console-address-change-smoke.yaml against a throwaway Host, and points the
# console somewhere else in the middle of it.
#
# The claim is narrow and it is the one that was broken: changing the address on
# the connection screen reconnects the console. Before the fix the dialog wrote
# the address down, said "Address saved. The stored key was kept.", and the
# caller rebuilt nothing, because it was gated on the key having changed. The
# operator moved the console and the console did not move.
#
# So the address this script saves is a loopback port where nothing is listening,
# and the assertions are about the console noticing. That makes the port a
# precondition rather than a detail: "Cannot reach the Host" is only a
# measurement if the address really does refuse, so this script checks that it
# does before it starts anything and refuses to run if something is already
# listening there. A run that picked a port by hope would produce a green that
# meant "the console reconnected to something".
#
# This run also measures ConsoleEnvironment's precedence, because it cannot be
# measured anywhere else: a stored address has to win over the harness's
# STYLOMAIL_HOST, or the console rebuilds to the Host it is already on and the
# address the operator saved is inert. That is what the first red run of this
# script turned out to be.
#
# The console is started on its own port rather than the harness default, so the
# silent port can be named as a constant in both this file and the script's YAML.
# Ports: 5391 is the Host, 5392 is the address nothing answers on.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The port is set before the source because console-harness.sh assigns it itself,
# and a `${CONSOLE_PORT:-...}` after the source keeps the harness's value rather than
# this one.
#
# The run directory is the same shape now, with the adoption removed: it is this
# script's own unless the caller exports one and sets CONSOLE_REUSE_RUN=1 to say so,
# because this script clears the directory it runs in. See console_runner_run_dir,
# called below the source and before the build. See also ux-scripts/README.md,
# "Writing a script", step 3.
CONSOLE_RUN_DEFAULT="/tmp/stylomail-console-address-change-ux"
export CONSOLE_PORT="${CONSOLE_PORT:-5391}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/address-change"

# The address the script saves. Named once here as well as in the YAML, because
# the check below is what makes it a measurement.
SILENT_PORT=5392

trap 'console_stop_host' EXIT INT TERM

# Anything listening on the silent port makes every assertion in this run
# meaningless in the direction that matters: the console would reach it, and
# "Cannot reach the Host" would fail for a reason that is not the console's.
#
# The probe is the harness's own, asked about SILENT_PORT rather than the port this
# run binds, because the saved address is http://127.0.0.1:5392 and the question
# that matters is whether a connect to THAT address succeeds. The lsof form this
# replaces asked something wider and cost more: it matched a listener on any
# address, and lsof is a process that sat in uninterruptible kernel sleep on this
# machine on 2026-10-01, where a signal is never delivered and a `wait` on it never
# returns. A /dev/tcp connect has no process in it to wedge. This preflight runs
# before the build, so an lsof wedged here would hold the run open before it had
# started anything to blame. See "Teardown" in ux-scripts/README.md.
#
# Three-valued, because a probe that cannot run must not read as free: the port is
# this script's precondition, and a run started against an unchecked address
# produces a green that means the console reconnected to something.
console_port_is_taken "$SILENT_PORT"
silent_status=$?

if (( silent_status == 0 )); then
    echo "Something is already listening on 127.0.0.1:$SILENT_PORT." >&2
    echo "This run saves that address because nothing answers there. Free the port," >&2
    echo "or change SILENT_PORT here and the address in the script's YAML together." >&2
    exit 1
elif (( silent_status == 2 )); then
    echo "The probe for 127.0.0.1:$SILENT_PORT could not run, so that address was never" >&2
    echo "asked about. That is not the same as it being free, so this run is refused" >&2
    echo "rather than started against an address nothing here has checked." >&2
    exit 1
fi

# Before the build: console_build_all stamps into $CONSOLE_RUN, so an inherited
# directory has to be refused before it rather than after. See console_runner_run_dir.
console_runner_run_dir "$CONSOLE_RUN_DEFAULT" || exit 2

console_build_all || exit 1

# Its own output directory, because the harness clears nothing it wrote before:
# two runners sharing one would leave whichever ran last as the only artifacts.
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE, address change without the key =="

console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the console, which will be pointed somewhere else mid-run =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-address-change-smoke.yaml \
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
