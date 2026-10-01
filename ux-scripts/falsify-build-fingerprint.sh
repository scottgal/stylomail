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
# Measured 2026-10-01: each mutation reddens exactly the cases named above.

set -uo pipefail

REPO="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
scratch="$REPO/.styloagent/scratch/desktop/test-build-fingerprint-is-load-bearing"

# This script deletes its own scratch tree at the start, so it must be certain
# that tree is the one it means. The same rule the check now enforces on itself.
if [[ "$scratch" != "$REPO/.styloagent/scratch/desktop/"* || "$scratch" == *"/.."* ]]; then
    echo "refusing to run: scratch resolves outside the lane's own scratch ($scratch)" >&2
    exit 2
fi

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

    printf '%-24s red: %s\n' "$name" "${red:-<none>}"

    if [[ "$red" != "$expect" ]]; then
        printf '%-24s EXPECTED red: %s\n' "" "$expect"
        printf '%s\n' "$out" | grep -E '^  FAIL' | sed 's/^/    /'
        return 1
    fi
    return 0
}

mutation_A() { perl -pi -e 's/identity\+="\$digest  \$name"\$'"'"'\\n'"'"'/identity+="\$name"\$'"'"'\\n'"'"'/' console-harness.sh; }
mutation_B() { perl -pi -e 's/^    rm -f "\$CONSOLE_RUN\/host-build\.id" "\$manifest"$/# removed for the falsification/' console-harness.sh; }
mutation_C() { perl -pi -e 's/^    printf .# \%s\\n. "\$\{out_dir\#\"\$CONSOLE_REPO\"\/\}".*$/# removed for the falsification/' console-harness.sh; }
mutation_D() { perl -0777 -pi -e 's/if ! console_assert_run_dir_is_ours "\$0"[^;]*; then\n    exit 2\nfi\n//' check-build-fingerprint.sh; }
mutation_E() { perl -pi -e 's/\$CONSOLE_RUN" == "\$allowed"\/\*/\$CONSOLE_RUN" == "\$allowed"*/' console-harness.sh; }
# F. The sibling check loses its call to the same guard. Its case 7 must go red:
#    what is under test there is that check-stop-host-bounded.sh CALLS the guard,
#    and before this mutation existed nothing went red when that call was removed.
mutation_F() { perl -0777 -pi -e 's/if ! console_assert_run_dir_is_ours "\$0"[^;]*; then\n    exit 2\nfi\n//' check-stop-host-bounded.sh; }

# A guard against the mutations themselves going stale: if a perl pattern stops
# matching, the "mutation" is a no-op and the pass reports <none>, which is a
# failure here rather than a quiet success.
mutation_A_applied() { ! grep -q 'identity+="\$digest' console-harness.sh; }
mutation_D_applied() { ! grep -qF 'console_assert_run_dir_is_ours "$0"' check-build-fingerprint.sh; }
mutation_E_applied() { grep -q '"\$allowed"\*' console-harness.sh; }
mutation_F_applied() { ! grep -qF 'console_assert_run_dir_is_ours "$0"' check-stop-host-bounded.sh; }

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

rm -rf "$scratch"

echo
if (( failures == 0 )); then
    echo "falsification: $passes_run mutations, each red on exactly its expected cases"
    exit 0
fi
echo "falsification: $failures of $passes_run pass(es) did not match expectation"
exit 1
