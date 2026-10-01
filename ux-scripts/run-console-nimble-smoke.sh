#!/usr/bin/env bash
# Runs console-nimble-smoke.yaml against a throwaway Host whose assessor works,
# after putting one held message in front of it.
#
# Why a run of its own rather than another section of the main smoke: every other
# console run reaches a Host that cannot assess. The main smoke's provider is
# pointed at an address that cannot answer, so policy declines every message and
# the queue listings stay empty; run-console-not-ready-smoke.sh reaches the Host
# with no assessor at all. Both are real states and both are the reason the
# middle pane was, until now, a pane no run had ever seen with a row in it.
#
# What that costs: this run needs a working local decision model (Ollama on 11435
# holding a `nimble` tag, per decisions 17 and 18), which is why it is not part of
# the main smoke. console_require_local_model refuses to start without one rather
# than declining every message for a reason nothing on the console's screen names.
#
# The message is submitted through POST /v1/submissions, which is the write path:
# it is what transfers delivery responsibility and returns a queue id, and a
# queue id is the only thing that reaches a queue listing. The assessment-only
# call the main smoke makes populates the ledger and leaves every listing empty,
# which is exactly the difference this run exists for.
#
# It asserts nothing about the message itself. Whether ux-scripts/nimble-held-message.eml
# is held is the model's answer and not this script's business; what this script
# does is refuse to drive the console when the route accepted something that was
# not held, because then the pane the yaml asserts about would be empty and every
# failure would look like a console defect.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Before the source, because console-harness.sh assigns CONSOLE_RUN itself and a
# `${CONSOLE_RUN:-...}` placed after it keeps the harness's value. See
# ux-scripts/README.md, "Writing a script", step 3.
export CONSOLE_RUN="${CONSOLE_RUN:-/tmp/stylomail-console-nimble-ux}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

# The one switch that decides whether this run is about anything. Set here rather
# than left to the caller: the default is the unreachable hosted provider, and a
# run that inherited it would post a message the route refuses with 503 and then
# assert about a pane nothing could fill.
export CONSOLE_PROVIDER=nimble

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/nimble"

# Under the tests project's fixtures rather than beside this script, and not by
# preference: .gitignore excludes *.eml with one negation for
# tests/**/fixtures/**/*.eml, which is the tree's way of saying that a tracked
# message is a fixture. Putting it in ux-scripts/ would need a second negation in
# a shared file for no gain, and the fixture is read by a test either way.
CONSOLE_MESSAGE="$CONSOLE_REPO/tests/StyloMail.Desktop.Tests/fixtures/nimble-held-message.eml"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_build_all || exit 1

# Its own scratch and output directories, so one run's screenshots cannot stand
# in for another's: the harness clears nothing it wrote before, so two runners
# sharing a directory would leave whichever ran last as the only artifacts.
# (The reason this file used to give was the main smoke's wholesale wipe of
# ux-results; see run-console-smoke.sh, where that wipe is now scoped to its own
# subdirectory.)
rm -rf "$CONSOLE_RUN" "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RUN" "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE with a working local assessor =="
console_start_host || exit 1

echo "== submitting $CONSOLE_MESSAGE through POST /v1/submissions =="
# The envelope is stated here rather than derived from the file: the route takes
# it as a field, and a script that parsed headers out of its own fixture would be
# measuring its parser as much as the Host.
console_seed_submission \
    "$CONSOLE_MESSAGE" \
    "billing@paypa1-secure.top" \
    "alice@example.test" || exit 1

# The route answered with a queue id, so something is in a queue, but not
# necessarily in the one the yaml opens. Held is what makes this run possible:
# an allowed message goes into normal delivery, which GET /v1/messages
# deliberately does not enumerate, so the listing would be empty and the pane's
# emptiness would be correct rather than a defect.
#
# The recipient's delivery state, which is the field the response carries, and
# the same state the listing derives from the same queue row.
if [[ "$CONSOLE_SUBMISSION_DELIVERY" != "Held" ]]; then
    echo "The route accepted the message and delivered it as" >&2
    echo "'$CONSOLE_SUBMISSION_DELIVERY', not Held." >&2
    echo "$CONSOLE_MESSAGE did not reach a held state on this Host, so the queue" >&2
    echo "listing this run drives the console over would be empty and every" >&2
    echo "failure would read as a console defect. The route's answer is in" >&2
    echo "$CONSOLE_RUN/submission-post.json." >&2
    exit 1
fi

echo "== the message is Held; driving the console =="

# The fixture is declined, and that is a statement about this run rather than a
# detail. The pane the yaml opens must be filled by the message-to-decision join,
# which is the whole thing under test; a decision body loaded from a file at
# window open would let the pane's assertions pass with the join never having
# run, and nothing in a screenshot would say so.
export CONSOLE_DECISION_FIXTURE=false

console_export_app_env
export ASPNETCORE_URLS=""

cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-nimble-smoke.yaml \
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
