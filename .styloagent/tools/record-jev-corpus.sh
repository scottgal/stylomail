#!/usr/bin/env bash
#
# Records the Jev corpus from a live call to the semantic provider.
#
# The credential is read from the TYPESAFE_API_KEY environment variable and nowhere else, and is
# NEVER printed, echoed, logged, or passed on a command line. This script reports that the variable
# was set and nothing about its value. Do not add a debug line that echoes it, including a
# "temporarily" one.
#
# Nothing is recorded without a credential: the script refuses rather than producing an empty corpus
# that looks like a finished recording.
#
# Usage:  .styloagent/tools/record-jev-corpus.sh [extra dotnet test arguments]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

# The environment variable is the only source. There is deliberately no file fallback: a key file
# at the repository root is a hard prohibition in the mission this lane grew out of, and the spec
# reserves that file for the overview's own live verification runs. Two rules pointing opposite
# ways would mean somebody deciding which wins, so this script reads one place and says so.
if [ -z "${TYPESAFE_API_KEY:-}" ]; then
    cat >&2 <<'MISSING'
No semantic provider credential is available, so nothing can be recorded.

Supply it as the TYPESAFE_API_KEY environment variable. To avoid the value reaching a command
line or any output, export it from a file:

    export TYPESAFE_API_KEY="$(cat /path/to/key)"

The key is never printed, logged, written into a fixture or committed.

Refusing rather than recording nothing: an empty corpus that looks like a result is worse than
no corpus.
MISSING
    exit 2
fi

# Presence only, never the value.
echo "credential source: TYPESAFE_API_KEY environment variable"

# dotnet is not on PATH in this environment.
export DOTNET_ROOT=/usr/local/share/dotnet
export PATH="/usr/local/share/dotnet:$PATH"

echo "recording into tests/fixtures/jev ..."

dotnet test tests/StyloMail.Jev.Tests/StyloMail.Jev.Tests.csproj \
    --nologo \
    --filter "FullyQualifiedName~JevCorpusRecordingTests" \
    "$@"

echo
echo "Recorded. Check the diff before committing: every .response.json is the provider's body"
echo "verbatim, and every .response.meta.json must say source=live and name the model it reported."
