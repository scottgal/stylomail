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

console_require_app || exit 1

# Built rather than `dotnet run` at launch time, so the pid the harness records
# is the Host itself and the cleanup can actually stop it.
dotnet build "$CONSOLE_REPO/src/StyloMail.Host/StyloMail.Host.csproj" -v quiet --nologo >/dev/null || exit 1

rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

# Cleared, not appended to. The harness writes screenshots without removing
# what a previous run left, so a step that stops running leaves its old image
# behind and it reads as this run's output. That cost real time: a screenshot
# from a passing run was read as evidence about a failing one.
rm -rf "$CONSOLE_REPO/ux-results"
mkdir -p "$CONSOLE_REPO/ux-results"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

console_export_app_env
export ASPNETCORE_URLS=""

# Something to group, so the sidebar's grouping is asserted rather than
# invisible behind a single ungrouped row.
console_seed_management

echo "== driving the console =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-smoke.yaml \
    --output ux-results
STATUS=$?

echo
echo "== result =="
if [[ -f ux-results/console-smoke.json ]]; then
    cat ux-results/console-smoke.json
fi
echo
echo "screenshots in ux-results/"

exit $STATUS
