#!/usr/bin/env bash
#
# Records the Jev corpus from a live call to the semantic provider.
#
# The credential comes from TYPESAFE_API_KEY or from the file that TYPESAFE_API_KEY_FILE names, and
# from nowhere else, and is NEVER printed, echoed, logged, or passed on a command line. This script
# reports which of the two routes was used and nothing about the value. Do not add a debug line that
# echoes it, including a "temporarily" one.
#
# Nothing is recorded without a credential: the script refuses rather than producing an empty corpus
# that looks like a finished recording.
#
# Usage:  tools/laya-bridge/record-jev-corpus.sh [extra dotnet test arguments]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

# Two routes, and the script never handles the value itself.
#
#   TYPESAFE_API_KEY       the value, set by a deployment
#   TYPESAFE_API_KEY_FILE  a PATH to a file holding it, read in-process by the test
#
# Prefer the path. A path is not a secret, so the command that runs this carries nothing sensitive,
# and the value is opened by the process that needs it rather than interpolated into a shell command.
# `export TYPESAFE_API_KEY="$(cat ...)"` is the form this exists to replace: it hands a secret to a
# visible command even when nothing prints it.
if [ -n "${TYPESAFE_API_KEY:-}" ]; then
    echo "credential source: TYPESAFE_API_KEY (set by the environment)"
elif [ -n "${TYPESAFE_API_KEY_FILE:-}" ]; then
    if [ ! -f "$TYPESAFE_API_KEY_FILE" ]; then
        echo "TYPESAFE_API_KEY_FILE is set to '$TYPESAFE_API_KEY_FILE', which is not a file." >&2
        exit 2
    fi
    # The path is printed. The value never is, and this script never reads it.
    echo "credential source: TYPESAFE_API_KEY_FILE (read in-process by the test)"
else
    cat >&2 <<'MISSING'
No semantic provider credential is available, so nothing can be recorded.

Set either:

    TYPESAFE_API_KEY        the key itself, as a deployment would inject it
    TYPESAFE_API_KEY_FILE   a PATH to a file holding the key

Prefer the path. The process that needs the key opens the file itself, so the value never reaches
a command line, an argument list or any output. Do not build TYPESAFE_API_KEY from the file in a
shell: that interpolates a secret into a visible command.

The key is never printed, logged, written into a fixture or committed.

Refusing rather than recording nothing: an empty corpus that looks like a result is worse than
no corpus.
MISSING
    exit 2
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
