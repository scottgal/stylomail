#!/usr/bin/env bash
# Runs console-quarantine-smoke.yaml against a throwaway Host whose assessor
# works, after putting one quarantined message in front of it, and releases that
# message from the console.
#
# Why a run of its own rather than another section of the Nimble smoke: reaching
# quarantine is not the same traffic as reaching held. The Nimble smoke's message
# fires enough weighted semantic dimensions to cross HoldThreshold; quarantine is
# the far side of QuarantineThreshold (0.80), and the fixture here
# (tests/StyloMail.Desktop.Tests/fixtures/quarantine-threshold-payment-change.eml)
# was written to cross it, measured at 0.8219178082191781 on a wiped Host. Held
# and quarantined differ by one field in the listing and by the one write action
# the console offers, so a run that only ever held could not reach the release
# surface at all.
#
# What that costs is the same as the Nimble smoke's: a local decision model
# (Ollama on 11435 holding a `nimble` tag, per decisions 17 and 18), which is why
# it is not part of the main smoke.
#
# The runner refuses to drive the console unless the route actually quarantined
# the message, for the reason the Nimble runner refuses unless it held: if the
# message were merely held, the listing this script drives would be empty, the
# Release button would never appear, and every failure below would look like a
# console defect rather than a fixture that stopped crossing the threshold.
#
# It asserts nothing about the message itself. Whether this fixture is
# quarantined is the model's answer and not this script's business, and the
# fixture is threshold-targeted by construction: it is a way to reach the state,
# and it must never be quoted as a detection rate.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Before the source, because console-harness.sh assigns CONSOLE_RUN itself and a
# `${CONSOLE_RUN:-...}` placed after it keeps the harness's value. See
# ux-scripts/README.md, "Writing a script", step 3.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-quarantine-ux}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

# The one switch that decides whether this run is about anything. Set here rather
# than left to the caller: the default is the unreachable hosted provider, and a
# run that inherited it would post a message the route refuses with 503 and then
# assert about a pane nothing could fill.
export CONSOLE_PROVIDER=nimble

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/quarantine"

# Under the tests project's fixtures rather than beside this script: .gitignore
# excludes *.eml with one negation for tests/**/fixtures/**/*.eml, which is the
# tree's way of saying that a tracked message is a fixture.
CONSOLE_MESSAGE="$CONSOLE_REPO/tests/StyloMail.Desktop.Tests/fixtures/quarantine-threshold-payment-change.eml"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_build_all || exit 1

# Its own scratch and output directories. The main smoke wipes ux-results
# wholesale, and each satellite run has its own subdirectory for the same reason.
rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE with a working local assessor =="
console_start_host || exit 1

echo "== submitting $CONSOLE_MESSAGE through POST /v1/submissions =="
# The envelope is stated here rather than derived from the file, for the reason
# the harness states it: the route takes mailFrom and rcptTo as fields, and a
# script that parsed headers out of its own fixture would be measuring its parser
# as much as the Host. These are the fixture's own From and To.
console_seed_submission \
    "$CONSOLE_MESSAGE" \
    "alerts@barclays-secure-verify.top" \
    "alice@example.test" || exit 1

# Quarantined is the whole precondition. The response carries the recipient's
# delivery state, and a Held answer means the fixture no longer crosses the
# threshold, which is a fact about the model rather than about the console.
if [[ "$CONSOLE_SUBMISSION_DELIVERY" != "Quarantined" ]]; then
    echo "The route accepted the message and delivered it as" >&2
    echo "'$CONSOLE_SUBMISSION_DELIVERY', not Quarantined." >&2
    echo "$CONSOLE_MESSAGE did not reach quarantine on this Host, so the listing" >&2
    echo "this run drives the console over would be empty, the Release button" >&2
    echo "would never appear, and every failure would read as a console defect." >&2
    echo "The route's answer is in $CONSOLE_RUN/submission-post.json." >&2
    exit 1
fi

echo "== the message is Quarantined; driving the console =="

# The fixture is declined, and that is a statement about this run rather than a
# detail: the decision the pane opens must come from the message-to-decision
# join, and a body loaded from a file would let the pane's assertions pass with
# the join never having run.
export CONSOLE_DECISION_FIXTURE=false

console_export_app_env
export ASPNETCORE_URLS=""

cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-quarantine-smoke.yaml \
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
