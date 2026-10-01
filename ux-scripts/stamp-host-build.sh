#!/usr/bin/env bash
# Stamps a REAL build, by calling console_record_build_fingerprint on a directory
# of real assemblies.
#
# THIS is the instrument. check-build-fingerprint.sh is the CHECK of it: that file
# writes its own stand-in assemblies and reports a verdict about the function, and
# on 2026-10-01 a fleet broadcast told every lane to "USE IT" to hash a build, which
# got the lanes a green about the function and nothing naming their build. This file
# exists so that instruction has somewhere correct to point.
#
# It builds nothing and starts no Host: it reads a directory that already exists.
#
# RUN IT, DO NOT SOURCE IT. The harness resolves the repo root from BASH_SOURCE[0],
# which is unset under zsh, this host's default shell, so sourcing it there leaves
# CONSOLE_REPO empty and the answer comes back "NOT RECORDED" for what looks like a
# directory problem. The shebang below is what makes it work; `ingress-` measured the
# failure and `overview-` broadcast the caveat.
#
# Usage:
#   ux-scripts/stamp-host-build.sh [directory]
#
#   directory defaults to the Host's own output directory, taken from
#   CONSOLE_HOST_APP. Give it another only if you know why: the identity is over
#   that directory's *.dll and nothing else.
#
# Writes, into $CONSOLE_RUN:
#   host-build.txt  the manifest: a directory header, then <sha256> <mtime> <name>
#                   per assembly, and a note that the mtime is the assembly's own
#                   timestamp rather than the build's.
#   host-build.id   the first 12 characters of the whole-build identity.
#
# The identity is a sha256 over "<digest>  <name>" for every assembly, so it covers
# content and name and deliberately NOT mtime: two builds of identical source are
# one build. What that buys, measured on 2026-10-01, is the difference between
# "nothing that matters rebuilt" and "something in the tree rebuilt" when a build
# lands at a new time. Cite the id, and cite the manifest beside it: the id alone
# cannot say which directory it was taken over, and the same assembly name exists in
# several bin directories as different files.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

# The stamp is written under the lane's own scratch by default, and an inherited
# CONSOLE_RUN is refused rather than adopted: this file writes into the directory it
# is given, and a caller's run directory is not this file's to write in. The guard is
# the same shared one the two checks call, so the three cannot drift apart.
export CONSOLE_RUN="${CONSOLE_RUN:-$REPO/.styloagent/scratch/desktop/stamp}"

# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

if ! console_assert_run_dir_is_ours "$0" "writes its stamp into"; then
    exit 2
fi

dir="${1:-$(dirname "$CONSOLE_HOST_APP")}"

if [[ ! -d "$dir" ]]; then
    echo "no directory at $dir, so there is nothing to stamp" >&2
    exit 2
fi

# console_record_build_fingerprint reads the DIRECTORY from CONSOLE_HOST_APP and never
# opens the file, so this is how a caller aims it at the directory it named. The name
# stays the Host's because that is what the function's own contract describes.
CONSOLE_HOST_APP="$dir/StyloMail.Host"

mkdir -p "$CONSOLE_RUN"
console_record_build_fingerprint

if [[ -f "$CONSOLE_RUN/host-build.id" ]]; then
    printf 'stamped into %s\n' "${CONSOLE_RUN#"$REPO"/}"
    printf 'manifest  %s\n' "${CONSOLE_RUN#"$REPO"/}/host-build.txt"
else
    # Not an error exit: "NOT RECORDED" is a real answer, and a caller scripting
    # this needs to see it as one rather than as a crash. The absence of the id file
    # is the signal, which is the same rule the fingerprint cases assert.
    printf 'nothing was stamped, so no id file was written\n' >&2
    exit 1
fi
