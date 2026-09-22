#!/usr/bin/env bash
#
# Records the Jev corpus from a live call to the semantic provider.
#
# The credential is read from the environment or from jevkey.pvt, and is NEVER printed, echoed,
# logged, or passed on a command line. This script reports which of the two places supplied it and
# nothing about its value. Do not add a debug line that echoes it, including a "temporarily" one.
#
# Nothing is recorded without a credential: the script refuses rather than producing an empty corpus
# that looks like a finished recording.
#
# Usage:  .styloagent/tools/record-jev-corpus.sh [extra dotnet test arguments]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

has_environment=0
if [ -n "${TYPESAFE_API_KEY:-}" ]; then
    has_environment=1
fi

has_key_file=0
if [ -f jevkey.pvt ]; then
    has_key_file=1
fi

if [ "$has_environment" -eq 0 ] && [ "$has_key_file" -eq 0 ]; then
    cat >&2 <<'MISSING'
No semantic provider credential is available, so nothing can be recorded.

Supply it as the TYPESAFE_API_KEY environment variable, or place it in jevkey.pvt at the
repository root. The key is never printed, logged, written into a fixture or committed, and
.pvt files are gitignored, which must not be relaxed.

Refusing rather than recording nothing: an empty corpus that looks like a result is worse than
no corpus.
MISSING
    exit 2
fi

# Presence only. Never the value.
if [ "$has_environment" -eq 1 ]; then
    echo "credential source: TYPESAFE_API_KEY environment variable"
else
    echo "credential source: jevkey.pvt at the repository root"
fi

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
