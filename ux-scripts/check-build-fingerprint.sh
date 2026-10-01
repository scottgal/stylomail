#!/usr/bin/env bash
# Checks console_record_build_fingerprint: what the harness records about the
# binary that produced a measurement.
#
# A check rather than a smoke, beside check-stop-host-bounded.sh and
# check-runner-gate.sh for the same reason: it needs no Host, no build, no console
# and no deployment, so there is no excuse for it not being run. It runs in
# seconds and it writes nothing outside its own scratch.
#
# The subject is a stamp, and a stamp is easy to write in a way that always
# succeeds. Three of these cases are about the ways it could look right and be
# meaningless:
#
#   - A digest that is stable is not a digest that reads the content. Case 2
#     changes one file's bytes and restores its mtime, so content moves and
#     nothing else does; if the fingerprint did not change, the number a run
#     cites would identify the build's shape rather than its build. Case 3 is the
#     other direction, and it exists so the two cannot be satisfied at once by a
#     hash over everything: with the content identical and only the mtime moved,
#     the identity must NOT move, or a rebuild of unchanged source reads as a
#     different engine.
#   - An absence that still leaves a number behind is not an absence. Case 5 puts
#     a previous run's id file in place and then fails to record: the stale id has
#     to go, because a reader who finds one will believe it, which is the exact
#     failure this whole stamp exists to prevent.
#   - A "NOT RECORDED" line is only honest if it is not also accompanied by a
#     digest. Every absence case asserts the digest is absent, not merely that a
#     sentence was printed.
#
# What it does NOT check: that the fingerprint is taken on a real run, or that it
# is taken at all. It exercises the function directly against a directory of
# stand-in assemblies. Whether console_build_all calls it on its success path is a
# reading of console-harness.sh, not something this file can see.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

# Its own run directory, so no real run's key file or artifact can be touched by
# this. Under the lane's scratch rather than /tmp for the reason README gives: an
# artifact in /tmp is shared, invisible to the fleet, and old enough to be misread
# as current.
export CONSOLE_RUN="${CONSOLE_RUN:-$REPO/.styloagent/scratch/desktop/build-fingerprint-scratch}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

failures=0
case_number=0

pass() { echo "  ok   $1"; }
fail() { echo "  FAIL $1"; failures=$((failures + 1)); }

rm -rf "$CONSOLE_RUN"
mkdir -p "$CONSOLE_RUN"

# The stand-in output directory. The function takes the directory from
# CONSOLE_HOST_APP rather than taking it as an argument, so that is what is set
# here; pointing it at scratch keeps the real Host's bin directory untouched, and
# this check never reads it.
out_dir="$CONSOLE_RUN/hostout"
CONSOLE_HOST_APP="$out_dir/StyloMail.Host"

id_file="$CONSOLE_RUN/host-build.id"
manifest="$CONSOLE_RUN/host-build.txt"

# A fingerprint is a timestamp-free digest of digests, and the property that makes
# it worth citing is that it moves when the thing it names moves. The shape is
# asserted too: an empty echo would otherwise satisfy a case that only checked the
# file was written, which is the vacuous form of this very check.
# An interval, not a literal: in an ERE `\{12\}` is a brace character, so the
# escaped form would only ever match a digest containing braces. The first draft
# of this file had it escaped and the case that notices is the control in case 4,
# which is there to catch exactly this shape of mistake.
digest_shape='^[0-9a-f]{12}$'

is_digest() {
    [[ "$1" =~ $digest_shape ]]
}

# Case 1: a directory of assemblies records a digest, a count, a manifest and an id.
case_number=$((case_number + 1))
echo "case $case_number: a directory of assemblies records a digest and a count"

mkdir -p "$out_dir"
printf 'policy-engine-one\n' > "$out_dir/StyloMail.Policy.dll"
printf 'host-one\n' > "$out_dir/StyloMail.Host.dll"
printf 'not-an-assembly\n' > "$out_dir/StyloMail.Host.deps.json"

output="$(console_record_build_fingerprint 2>&1)"
first_id="$(cat "$id_file" 2>/dev/null)"

if is_digest "$first_id"; then
    pass "recorded an id of the expected shape: $first_id"
else
    fail "the id file does not hold a 12-hex digest: '$first_id'"
fi

if [[ "$output" == *"over 2 assemblies"* ]]; then
    pass "the count is the two .dll files and not the .json beside them"
else
    fail "the count is not 2 assemblies: $output"
fi

lines="$(wc -l < "$manifest" 2>/dev/null | tr -d ' ')"
if [[ "$lines" == "2" ]]; then
    pass "the manifest has one line per assembly"
else
    fail "the manifest has $lines line(s), expected 2"
fi

# The manifest is what a reader needs when two runs disagree about a number, so it
# has to carry a time as well as a digest. The id is content only (case 3) but the
# manifest is the record of which build, and "taken before the change being
# measured" is a question about the file's own time.
if grep -qE '^[0-9a-f]{64}  [0-9]+  StyloMail\.Policy\.dll$' "$manifest"; then
    pass "the manifest line carries a digest, an mtime and a name"
else
    fail "the manifest does not carry digest, mtime and name: $(head -1 "$manifest")"
fi

# Case 2: the same name and the same size and the same mtime, different bytes.
# Same size matters: a fingerprint computed over names and file sizes would pass a
# test that changed the length, so this one does not change it. The mtime is
# restored afterwards, so the only thing that moved is the content.
case_number=$((case_number + 1))
echo "case $case_number: the digest moves when the bytes move and nothing else does"

stamp_ref="$CONSOLE_RUN/stamp-ref"
touch -r "$out_dir/StyloMail.Policy.dll" "$stamp_ref"

before_mtime="$(console_file_mtime "$out_dir/StyloMail.Policy.dll")"

# 'policy-engine-one' to 'policy-engine-two': different bytes, identical length.
printf 'policy-engine-two\n' > "$out_dir/StyloMail.Policy.dll"
touch -r "$stamp_ref" "$out_dir/StyloMail.Policy.dll"

after_mtime="$(console_file_mtime "$out_dir/StyloMail.Policy.dll")"

if [[ "$before_mtime" == "$after_mtime" ]]; then
    pass "the mtime was restored, so content is the only variable"
else
    fail "the mtime moved ($before_mtime to $after_mtime), so this case measures the wrong thing"
fi

console_record_build_fingerprint >/dev/null 2>&1
second_id="$(cat "$id_file" 2>/dev/null)"

if is_digest "$second_id" && [[ "$second_id" != "$first_id" ]]; then
    pass "the id moved with the content: $first_id to $second_id"
else
    fail "the id did not move with the content: still '$second_id'"
fi

# Case 3: the other direction, and it is a decision rather than a law. A rebuild
# of unchanged source is the same engine, so the identity must not move; if it
# did, every lane would have to re-argue a fingerprint that had changed under it
# for no reason. The manifest still records the new time, which is how a reader
# answers "was this build taken before the change being measured".
case_number=$((case_number + 1))
echo "case $case_number: a new mtime alone leaves the identity alone"

# A fixed timestamp rather than `touch` with no argument, because everything in
# this file runs inside one second: a bare touch left the mtime exactly where it
# was, and the case passed its id check while asserting a time change that had not
# happened. The precondition is checked for that reason, and the value is read
# back rather than assumed, since `touch -t` interprets in local time.
touch -t 202001010000 "$out_dir/StyloMail.Policy.dll"
moved_to="$(console_file_mtime "$out_dir/StyloMail.Policy.dll")"

if [[ "$moved_to" != "$before_mtime" ]]; then
    pass "the mtime moved, from $before_mtime to $moved_to"
else
    fail "the mtime did not move, so this case cannot be read"
fi

third_id="$(console_record_build_fingerprint >/dev/null 2>&1; cat "$id_file" 2>/dev/null)"

if [[ "$third_id" == "$second_id" ]]; then
    pass "identical content keeps the same id after a rebuild"
else
    fail "the id moved ($second_id to $third_id) with the content unchanged"
fi

if grep -q "StyloMail.Policy.dll" "$manifest" && grep -q "  $moved_to  " "$manifest"; then
    pass "the manifest records the new time even though the id did not move"
else
    fail "the manifest does not carry $moved_to: $(grep 'StyloMail.Policy.dll' "$manifest")"
fi

# Case 4: an output directory with no assemblies is not a build with a fingerprint.
case_number=$((case_number + 1))
echo "case $case_number: an output directory with no assemblies"

empty_dir="$CONSOLE_RUN/empty"
mkdir -p "$empty_dir"
printf 'not-an-assembly\n' > "$empty_dir/StyloMail.Host.deps.json"
CONSOLE_HOST_APP="$empty_dir/StyloMail.Host"

output="$(console_record_build_fingerprint 2>&1)"

if [[ "$output" == *"NOT RECORDED"* ]]; then
    pass "it reports the absence rather than a number"
else
    fail "it did not report an absence: $output"
fi

if is_digest "$(cat "$id_file" 2>/dev/null)"; then
    fail "an id was left behind for a directory it could not fingerprint"
else
    pass "no id from this attempt"
fi

# Control: the same absence check, against a recording that did work. Without it
# the two lines above prove nothing, because a detector that had stopped matching
# would report "no id" just as happily as a correct one. `second_id` is a real
# recording from this same function, so the detector is shown to see one.
if is_digest "$second_id"; then
    pass "the same check sees a digest in a recording that succeeded"
else
    fail "the absence check cannot tell a digest from an absence, so it controls nothing"
fi

# Case 5: an absence must not leave a previous run's id behind. This is the one
# that matters, because the id file is the artifact a reader cites. If a run that
# could not record its build left the last run's number in place, the fleet's own
# rule ("if you cannot say which build you measured, say that") would be broken by
# the file the rule was written for.
case_number=$((case_number + 1))
echo "case $case_number: a failed recording does not leave a stale id behind"

mkdir -p "$out_dir"
printf 'policy-engine-one\n' > "$out_dir/StyloMail.Policy.dll"
printf 'host-one\n' > "$out_dir/StyloMail.Host.dll"
CONSOLE_HOST_APP="$out_dir/StyloMail.Host"
console_record_build_fingerprint >/dev/null 2>&1
stale_id="$(cat "$id_file" 2>/dev/null)"

if is_digest "$stale_id"; then
    pass "a good recording is in place to go stale: $stale_id"
else
    fail "could not place a stale id to test against"
fi

CONSOLE_HOST_APP="$CONSOLE_RUN/never-created/StyloMail.Host"
output="$(console_record_build_fingerprint 2>&1)"

if [[ "$output" == *"NOT RECORDED"* ]]; then
    pass "a missing directory is reported, not invented"
else
    fail "a missing directory did not report an absence: $output"
fi

if [[ -f "$id_file" ]]; then
    fail "the stale id survived an attempt that could not record: $(cat "$id_file")"
else
    pass "the stale id is gone, so no reader can cite it as this run's build"
fi

if [[ -f "$manifest" ]]; then
    fail "the stale manifest survived an attempt that could not record"
else
    pass "the stale manifest is gone as well"
fi

# Case 6: no shasum. The guard exists because a missing shasum would otherwise
# write a manifest of empty digests, hash that, and print a stable number that
# names nothing: plausible, reproducible and meaningless, which is worse than no
# stamp at all.
case_number=$((case_number + 1))
echo "case $case_number: no shasum on PATH is reported, not turned into a number"

mkdir -p "$out_dir"
printf 'policy-engine-one\n' > "$out_dir/StyloMail.Policy.dll"
printf 'host-one\n' > "$out_dir/StyloMail.Host.dll"
CONSOLE_HOST_APP="$out_dir/StyloMail.Host"

output="$(PATH=/nonexistent console_record_build_fingerprint 2>&1)"

if [[ "$output" == *"NOT RECORDED"* ]]; then
    pass "it reports that it cannot record rather than recording nothing"
else
    fail "with no shasum it said: $output"
fi

if is_digest "$(cat "$id_file" 2>/dev/null)"; then
    fail "a digest was written by a run that had no shasum to compute one"
else
    pass "no digest was invented"
fi

rm -rf "$CONSOLE_RUN"

echo
if (( failures == 0 )); then
    echo "build-fingerprint: $case_number cases, 0 failures"
    exit 0
fi

echo "build-fingerprint: $case_number cases, $failures failures"
exit 1
