#!/usr/bin/env bash
# Generates a corpus batch with tools/corpus/corpus.py, seeds it through the
# Host's own authenticated routes, and drives the console over the Host that
# resulted.
#
# This is the runner that makes the corpus-to-console seam real. The switch
# (CONSOLE_CORPUS, read by console_seed_corpus) has been wired since 1 Oct 2026
# and no committed runner had ever called it, so the seam had never been run
# end to end: ux-scripts/README.md said so in as many words, "a verified
# invocation is not a verified run".
#
# Two questions, two instruments, and that split is the design rather than an
# accident of implementation:
#
#   * the corpus's own checker (corpus.py check) answers "did every planted fact
#     come back reported by the pipeline", reading the ledger by the message's
#     own join key. It is the tool's oracle over its own facts, and this script
#     requires it to return 0 before it reports anything;
#   * the console smoke (console-corpus-smoke.yaml) answers "does the operator's
#     screen show that row and that fact", which is this lane's half and which no
#     route-level check can answer.
#
# Neither is a substitute for the other, and the run is green only if both are.
#
# What it costs is the same as the quarantine smoke's: a local decision model
# (Ollama on 11435 holding a `nimble` tag, per decisions 17 and 18), because a
# decision is written when a message is assessed and an unreachable provider
# assesses nothing. console_start_host enforces that when CONSOLE_PROVIDER is
# nimble rather than letting the run fail later as an empty screen.
#
# It asserts nothing about how many rows any listing holds. Reaching a queue
# state is the model's answer and not this script's business (the corpus is
# threshold-targeted by construction and must never be quoted as a detection
# rate), so the count-bearing assertions live on the ledger, where every assessed
# message is a row.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# This script's own run directory, and it is not assigned here before the
# source: `${CONSOLE_RUN:-...}` keeps an inherited value, which is how
# `export CONSOLE_RUN=<another run>` used to delete that run. The default goes
# to console_runner_run_dir below the source, which refuses an inherited
# directory unless the caller sets CONSOLE_REUSE_RUN=1. See ux-scripts/README.md,
# "Writing a script", step 3.
CONSOLE_RUN_DEFAULT="/tmp/stylomail-console-corpus-ux"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

# The one switch that decides whether this run is about anything. Set here rather
# than left to the caller: the default is the unreachable hosted provider, and a
# run that inherited it would post a batch the routes refuse and then assert
# about a ledger nothing could fill.
export CONSOLE_PROVIDER=nimble

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/corpus"

# The batch is a parameter of the run, not whatever the last execution left
# behind, and all three are printed below so a re-run next week is the same
# corpus or a visible difference. The seed is a constant rather than a clock:
# determinism is the property that makes a failure here reproducible.
CONSOLE_CORPUS_SEED="${CONSOLE_CORPUS_SEED:-20261001}"
CONSOLE_CORPUS_COUNT="${CONSOLE_CORPUS_COUNT:-4}"
CONSOLE_CORPUS_PROFILE="${CONSOLE_CORPUS_PROFILE:-quarantine}"

CONSOLE_CORPUS_TOOL="$CONSOLE_REPO/tools/corpus/corpus.py"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

# Before the build: console_build_all stamps into $CONSOLE_RUN, so an inherited
# directory has to be refused before it rather than after.
console_runner_run_dir "$CONSOLE_RUN_DEFAULT" || exit 2

# Everything derived from $CONSOLE_RUN is computed BELOW that call and not above
# it, and that ordering decides the result rather than being tidy. The harness falls back to the
# main smoke's run directory at source time, so a path built before this point is
# built from the wrong directory: the first version of this script put its batch
# in /tmp/stylomail-console-ux and, worse, handed the checker that run's
# principal key, so the checker authenticated as the wrong principal and every
# planted fact came back "no decision could be read back from the ledger". The
# seeding itself used the right key, because console_seed_corpus builds its own
# path at call time; the two halves disagreed and the run said so.
CONSOLE_CORPUS_BATCH="$CONSOLE_RUN/data/corpus"
CONSOLE_CORPUS_MANIFEST="$CONSOLE_CORPUS_BATCH/manifest.json"
CONSOLE_CORPUS_KEY="$CONSOLE_RUN/data/principal.key"

console_build_all || exit 1

# Its own scratch and output directories. Each satellite run has its own
# subdirectory because the harness clears nothing it wrote before, so sharing one
# would leave whichever ran last as the only artifacts.
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

if [[ ! -f "$CONSOLE_CORPUS_TOOL" ]]; then
    echo "No corpus tool at $CONSOLE_CORPUS_TOOL." >&2
    echo "This runner needs corpus-'s generator; it is not this lane's to ship." >&2
    exit 1
fi

echo "== starting the throwaway Host on $CONSOLE_BASE with a working local assessor =="
console_start_host || exit 1

# The key the corpus authenticates with is the Host's own per-run principal key,
# handed over as a PATH and never as a value. console_seed_corpus passes it, and
# the checker below reads the same file, so both speak as the same principal.
if [[ ! -s "$CONSOLE_CORPUS_KEY" ]]; then
    echo "The Host started without writing a principal key at $CONSOLE_CORPUS_KEY," >&2
    echo "so nothing here could be authenticated. The batch is not seeded." >&2
    exit 1
fi

echo "== generating a corpus batch: seed=$CONSOLE_CORPUS_SEED count=$CONSOLE_CORPUS_COUNT profile=$CONSOLE_CORPUS_PROFILE =="
python3 "$CONSOLE_CORPUS_TOOL" generate \
    --seed "$CONSOLE_CORPUS_SEED" \
    --count "$CONSOLE_CORPUS_COUNT" \
    --out "$CONSOLE_CORPUS_BATCH" \
    --profile "$CONSOLE_CORPUS_PROFILE" || exit 1

if [[ ! -s "$CONSOLE_CORPUS_MANIFEST" ]]; then
    echo "The generator wrote no manifest at $CONSOLE_CORPUS_MANIFEST." >&2
    echo "Without it there is no record of what was planted, and a run that seeded" >&2
    echo "something it cannot describe is not a reproducible run." >&2
    exit 1
fi

echo "== seeding the batch through the Host's own authenticated routes =="
export CONSOLE_CORPUS="$CONSOLE_CORPUS_BATCH"
console_seed_corpus || exit 1

# The tool's oracle over its own facts, and it runs BEFORE the console is driven
# because the two answer different questions: this one decides whether the batch
# is worth showing, and a run that drove the screen over a batch the pipeline
# never reported would produce a screenshot of a defect rather than evidence.
#
# It also refuses to report success on a batch where no message could be checked
# at all, which is the shape a silently empty run would otherwise take.
echo "== checking that every planted fact came back reported =="
python3 "$CONSOLE_CORPUS_TOOL" check \
    --base-url "$CONSOLE_BASE" \
    --key-file "$CONSOLE_CORPUS_KEY" \
    --manifest "$CONSOLE_CORPUS_MANIFEST"
CHECK_STATUS=$?

echo "== driving the console =="

# The pane must get its decision from the message-to-decision join and not from a
# file, or every assertion in the script could pass with the join never having
# run.
export CONSOLE_DECISION_FIXTURE=false

console_export_app_env
export ASPNETCORE_URLS=""

cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-corpus-smoke.yaml \
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
APP_STATUS=$?

if [[ $CHECK_STATUS -ne 0 ]]; then
    echo
    echo "The console half may have passed, but the corpus half did not: the checker" >&2
    echo "exited $CHECK_STATUS over the batch this run seeded, so at least one planted" >&2
    echo "fact is not in the findings the Host reported. Both halves are the gate." >&2
    exit 1
fi

exit $APP_STATUS
