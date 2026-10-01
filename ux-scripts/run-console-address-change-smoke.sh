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

# Before the source, because console-harness.sh sets these itself and a
# `${VAR:-...}` after it keeps the harness's value. See ux-scripts/README.md,
# "Writing a script", step 3.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-address-change-ux}"
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
listening="$(lsof -nP -iTCP:"$SILENT_PORT" -sTCP:LISTEN 2>/dev/null | tail -n +2)"

if [[ -n "$listening" ]]; then
    echo "Something is already listening on 127.0.0.1:$SILENT_PORT:" >&2
    echo "$listening" >&2
    echo "This run saves that address because nothing answers there. Free the port," >&2
    echo "or change SILENT_PORT here and the address in the script's YAML together." >&2
    exit 1
fi

console_build_all || exit 1

# Its own output directory, because the main smoke wipes ux-results wholesale.
rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

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
