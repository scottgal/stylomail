#!/usr/bin/env bash
# The record of the falsification, kept rather than run: it is evidence that
# check-build-fingerprint.sh can go red, not a check to run in a loop.
#
# It lives here rather than in scratch because scratch is gitignored: evidence a
# fresh checkout cannot read is not evidence, which this repo already learned once
# when the fleet's mutation harness had no versioned home.
#
# It copies the harness and the check into a scratch directory and mutates the
# COPY, so the shared tree is never edited.
#
# ONE MUTATION PER PASS. An earlier version of this file applied every mutation to
# a single copy and reported the total, which was readable only while each
# mutation broke a disjoint set of cases. The moment two mutations target the same
# case (D and E both target case 8) a red becomes unattributable: the output says
# case 8 failed, and says nothing about which property failed. So each pass now
# gets its own copy and prints only the cases ITS mutation turned red, and the
# summary at the end checks the observed set against the expected one.
#
#   A. The identity stops covering content (names only). Case 2 must go red: its
#      whole purpose is that the id moves when the bytes move.
#   B. The absence paths stop clearing the previous run's stamp. Cases 4, 5 and 6
#      must go red: every one of them asserts no digest is left behind.
#   C. The directory header is dropped. Cases 1 and 7 must go red: the digests
#      would no longer say which file they are digests of.
#   D. The guard call is dropped from the check. Case 8 must go red: the caller's
#      run directory is adopted and deleted, which is the HIGH defect itself.
#   E. The guard's pattern loses its trailing slash, so
#      `.styloagent/scratch/desktop-decoy` matches the lane's scratch prefix. Case
#      8 must go red for the same reason. This is the mutation that says whether
#      the slash is load-bearing or decoration.
#
#   G. The canonicaliser returns its argument unchanged, leaving only the string
#      comparison. Case 8 must go red on the symlink assertion.
#
#   H. The port-value validation is disabled. The sibling's case 8 must go red.
#
#   I. The probe's allocation test is removed, leaving the two-valued connect that
#      reads an unallocatable descriptor as "nothing is listening". The sibling's
#      case 9 must go red, and ONLY case 9: on an unsqueezed table the connect still
#      answers, so nothing else about the probe changes.
#   J. The probe is stuck on the third value, always returning 2. This is the other
#      half of I and it is not optional: a probe that always refuses would satisfy
#      case 9's central assertion (the refusal) while quietly satisfying the
#      free-direction case too, so without J a probe that answers nothing but
#      "cannot tell" would look defended. Case 4 must go red, and it holds BOTH port
#      directions rather than one case each; case 6 must go red, because its refusal
#      message names the holder only on the taken path; and case 9 on its own
#      control, which asserts the probe is not stuck before the squeeze.
#
# Measured 2026-10-01: each mutation reddens exactly the cases named above.

set -uo pipefail

REPO="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
scratch="$REPO/.styloagent/scratch/desktop/test-build-fingerprint-is-load-bearing"

# Sourced for `console_canonical_path` alone, and from the ORIGINAL tree rather than
# from the copy each pass runs: mutation G neuters the copy's canonicaliser, and a
# guard that a mutation can switch off is not a guard.
source "$REPO/ux-scripts/console-harness.sh"

# This script deletes its own scratch tree, so it must be certain that tree is the one
# it means. The test here used to be lexical: a literal prefix plus a `..` substring, so
# a symlink at ANY component of the path matched the string while the `rm -rf` below
# resolved somewhere else entirely. `nimble-` raised it from an automated review on
# 1 Oct, and it is the same defect `console_assert_run_dir_is_ours` was already repaired
# for: this file, which exists to prove that guard is load-bearing, had the weaker form.
console_scratch_is_ours() {
    local path="$1" base="$REPO/.styloagent/scratch/desktop"
    local path_canon base_canon
    path_canon="$(console_canonical_path "$path")" || path_canon=""
    base_canon="$(console_canonical_path "$base")" || base_canon=""
    [[ -n "$path_canon" && -n "$base_canon" && "$path_canon" == "$base_canon"/* ]]
}

if ! console_scratch_is_ours "$scratch"; then
    echo "refusing to run: scratch does not resolve under the lane's own scratch" >&2
    echo "  scratch: $scratch -> $(console_canonical_path "$scratch" || echo unreadable)" >&2
    echo "  base:    .styloagent/scratch/desktop under $REPO" >&2
    exit 2
fi

# The guard's own control, in both directions, because a guard whose refusal has never
# been exercised cannot be told apart from one that refuses everything: a symlink inside
# the base that points OUT of it must be refused, and a plain path inside must be allowed.
control_link="$REPO/.styloagent/scratch/desktop/.guard-control-outside"
control_target="$REPO/.styloagent/scratch/desktop-guard-control-target"
rm -rf "$control_link" "$control_target"
mkdir -p "$control_target"
ln -s "$control_target" "$control_link"
if console_scratch_is_ours "$control_link/child"; then
    echo "refusing to run: the scratch guard allowed a symlink out of the base, so it is decoration" >&2
    rm -rf "$control_link" "$control_target"
    exit 2
fi
if ! console_scratch_is_ours "$REPO/.styloagent/scratch/desktop/plain-dir"; then
    echo "refusing to run: the scratch guard refused a path inside the base, so it refuses everything" >&2
    rm -rf "$control_link" "$control_target"
    exit 2
fi
rm -rf "$control_link" "$control_target"

# Applies a mutation to the copy in the current directory, then runs the check
# against it and prints the cases that went red, as "<case>: <first failure>".
pass() {
    local name="$1" expect="$2" subject="$3" dir="$scratch/$1"
    shift 3
    passes_run=$((passes_run + 1))

    rm -rf "$dir"
    mkdir -p "$dir"
    cp "$REPO/ux-scripts/console-harness.sh" "$REPO/ux-scripts/check-build-fingerprint.sh" \
       "$REPO/ux-scripts/check-stop-host-bounded.sh" "$dir/"

    ( cd "$dir" && "$@" )

    # No CONSOLE_RUN here, deliberately. The check derives its repo root from its
    # own location, so a copy in a subdirectory believes the repo root is $dir and
    # its own default run directory is inside that. Passing an explicit
    # CONSOLE_RUN="$dir/run" put the run outside the copy's own view of its
    # scratch, the guard refused before a single case ran, and every pass reported
    # <none>: it was measuring the refusal, not the mutation. Letting the copy use
    # its own default keeps the whole run inside $dir, which is scratch anyway.
    local out
    out="$( cd "$dir" && env -u CONSOLE_RUN bash "./$subject" 2>&1 )"

    # Map each failure to the case it happened in. The check prints "case N:" as a
    # header and "  FAIL ..." under it, so the case number is carried down.
    local red
    red="$( printf '%s\n' "$out" | awk '
        /^case [0-9]+:/ { n = $2; sub(":", "", n) }
        /^  FAIL/       { if (!seen[n]++) printf "%s ", n }
    ' )"

    # The subject is named because two of these mutations redden a case 8 that is
    # not the same case 8: D, E and G redden case 8 of the fingerprint check and H
    # reddens case 8 of the sibling. A bare "8" cannot be attributed to a file, so
    # the line names the file the number belongs to.
    printf '%-30s red: %-6s %s\n' "$name" "${red:-<none>}" "$subject"

    if [[ "$red" != "$expect" ]]; then
        printf '%-30s EXPECTED red: %s\n' "" "$expect"
        printf '%s\n' "$out" | grep -E '^  FAIL' | sed 's/^/    /'
        return 1
    fi
    return 0
}

mutation_A() { perl -pi -e 's/identity\+="\$digest  \$name"\$'"'"'\\n'"'"'/identity+="\$name"\$'"'"'\\n'"'"'/' console-harness.sh; }
mutation_B() { perl -pi -e 's/^    rm -f "\$CONSOLE_RUN\/host-build\.id" "\$manifest"$/# removed for the falsification/' console-harness.sh; }
mutation_C() { perl -pi -e 's/^    printf .# \%s\\n. "\$\{out_dir\#\"\$CONSOLE_REPO\"\/\}".*$/# removed for the falsification/' console-harness.sh; }
mutation_D() { perl -0777 -pi -e 's/if ! console_assert_run_dir_is_ours "\$0"[^;]*; then\n    exit 2\nfi\n//' check-build-fingerprint.sh; }
mutation_E() { perl -pi -e 's/"\$run_canon" == "\$allowed_canon"\/\*/"\$run_canon" == "\$allowed_canon"*/' console-harness.sh; }
# F. The sibling check loses its call to the same guard. Its case 7 must go red:
#    what is under test there is that check-stop-host-bounded.sh CALLS the guard,
#    and before this mutation existed nothing went red when that call was removed.
mutation_F() { perl -0777 -pi -e 's/if ! console_assert_run_dir_is_ours "\$0"[^;]*; then\n    exit 2\nfi\n//' check-stop-host-bounded.sh; }

# G. The canonicaliser returns its argument unchanged. Case 8's symlink assertion must
#    go red: with no resolution, a path through a symlink inside the base reads as
#    being inside the base.
mutation_G() { perl -pi -e 's/^    local path="\$1"$/    local path="\$1"; printf "%s\\n" "\$path"; return 0/' console-harness.sh; }

# H. The port-value validation in console_start_host is disabled, leaving the connect
#    probe to answer for a port that was never probed. Case 8 of the sibling check must
#    go red: nimble- broadcast this shape against their netstat gate on 2026-10-01.
mutation_H() { perl -pi -e 's/^    if \[\[ ! "\$CONSOLE_PORT" =~ \^\[0-9\]\+\$ \]\]; then$/    if false; then/' console-harness.sh; }

# I. The third value is removed at its source: the function becomes the retired
#    two-valued one, where a connect that cannot allocate a descriptor is
#    indistinguishable from a quiet port. The sibling's case 9 must go red.
mutation_I() { perl -0777 -pi -e 's/    if ! : < \/dev\/null 2>\/dev\/null; then\n        return 2\n    fi\n//' console-harness.sh; }

# J. The probe is stuck on the third value, so it never answers about the port at
#    all. Cases 1 and 2 of the sibling must go red, and case 9 on its control.
mutation_J() { perl -pi -e 's/^    if ! : < \/dev\/null 2>\/dev\/null; then$/    if true; then/' console-harness.sh; }

# A guard against the mutations themselves going stale: if a perl pattern stops
# matching, the "mutation" is a no-op and the pass reports <none>, which is a
# failure here rather than a quiet success.
mutation_A_applied() { ! grep -q 'identity+="\$digest' console-harness.sh; }
mutation_D_applied() { ! grep -qF 'console_assert_run_dir_is_ours "$0"' check-build-fingerprint.sh; }
mutation_E_applied() { grep -q '"\$allowed_canon"\*' console-harness.sh; }
mutation_H_applied() { grep -q '^    if false; then$' console-harness.sh; }
mutation_G_applied() { grep -q 'printf "%s\\n" "\$path"; return 0' console-harness.sh; }
mutation_F_applied() { ! grep -qF 'console_assert_run_dir_is_ours "$0"' check-stop-host-bounded.sh; }
mutation_I_applied() { ! grep -qF ': < /dev/null 2>/dev/null' console-harness.sh; }
mutation_J_applied() { grep -q '^    if true; then$' console-harness.sh; }

fingerprint=check-build-fingerprint.sh
stop=check-stop-host-bounded.sh

failures=0
passes_run=0
pass "A names-only identity" "2 "      "$fingerprint" mutation_A || failures=$((failures + 1))
pass "B no clearing on absence" "4 5 6 " "$fingerprint" mutation_B || failures=$((failures + 1))
pass "C header dropped" "1 7 "         "$fingerprint" mutation_C || failures=$((failures + 1))
pass "D guard call dropped" "8 "       "$fingerprint" mutation_D || failures=$((failures + 1))
pass "E slash dropped" "8 "            "$fingerprint" mutation_E || failures=$((failures + 1))
pass "F sibling guard dropped" "7 "    "$stop"        mutation_F || failures=$((failures + 1))
pass "G canonicaliser neutered" "8 "   "$fingerprint" mutation_G || failures=$((failures + 1))
pass "H port validation dropped" "8 "  "$stop"        mutation_H || failures=$((failures + 1))
pass "I allocation test removed" "9 "  "$stop"        mutation_I || failures=$((failures + 1))
pass "J probe stuck on cannot tell" "4 6 9 " "$stop"   mutation_J || failures=$((failures + 1))

# The preconditions, checked last so the reports above are printed either way.
did_it_apply() {
    local dir="$scratch/$1" fn="$2"
    if ! ( cd "$dir" && "$fn" ); then
        echo "mutation $1 did not apply: the pattern no longer matches the source, so its pass proved nothing" >&2
        failures=$((failures + 1))
    fi
}

did_it_apply "A names-only identity" mutation_A_applied
did_it_apply "D guard call dropped" mutation_D_applied
did_it_apply "E slash dropped" mutation_E_applied
did_it_apply "F sibling guard dropped" mutation_F_applied
did_it_apply "G canonicaliser neutered" mutation_G_applied
did_it_apply "H port validation dropped" mutation_H_applied
did_it_apply "I allocation test removed" mutation_I_applied
did_it_apply "J probe stuck on cannot tell" mutation_J_applied

rm -rf "$scratch"

echo
if (( failures == 0 )); then
    echo "falsification: $passes_run mutations, each red on exactly its expected cases"
    exit 0
fi
echo "falsification: $failures of $passes_run pass(es) did not match expectation"
exit 1
