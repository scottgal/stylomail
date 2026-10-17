#!/usr/bin/env bash
# Drives the desktop against an already-running Host. This runner never starts,
# seeds, stops, probes, or otherwise owns that Host: its only inputs are the
# Host base URL and a path to a file containing the API key.
#
# Required configuration (paths are safe to report; key contents never are):
#   CONSOLE_EXTERNAL_HOST_URL=https://host.example
#   CONSOLE_EXTERNAL_API_KEY_FILE=/absolute/path/to/api-key
# Optional:
#   CONSOLE_EXTERNAL_SCRIPT=ux-scripts/console-smoke.yaml
#   CONSOLE_EXTERNAL_OUTPUT=/absolute/or-repo-relative/output-directory

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
base="${CONSOLE_EXTERNAL_HOST_URL:-}"
key_file="${CONSOLE_EXTERNAL_API_KEY_FILE:-}"
script="${CONSOLE_EXTERNAL_SCRIPT:-ux-scripts/console-smoke.yaml}"
run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
output="${CONSOLE_EXTERNAL_OUTPUT:-$REPO/ux-results/console-external-host/$run_id}"

case "$base" in
    http://*|https://*) ;;
    *) echo "CONSOLE_EXTERNAL_HOST_URL must be an http(s) base URL." >&2; exit 2 ;;
esac

if [[ -z "$key_file" || ! -f "$key_file" || ! -r "$key_file" ]]; then
    echo "CONSOLE_EXTERNAL_API_KEY_FILE must name a readable regular file." >&2
    exit 2
fi

if [[ ! -f "$REPO/$script" ]]; then
    echo "CONSOLE_EXTERNAL_SCRIPT must name a repository script file." >&2
    exit 2
fi

# Command substitution deliberately has no diagnostic output. The value becomes
# a Debug-only console setting only; it is neither echoed nor passed as argv.
# Accept either a raw one-line key or curl's `X-StyloMail-Key: …` header form.
# Parsing emits only into this command substitution, never into the runner log.
key="$(python3 - "$key_file" <<'PYEOF'
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read().strip()
match = re.search(r"X-StyloMail-Key\s*:\s*([^\"\r\n]+)", text, re.I)
print((match.group(1).strip() if match else text).strip(), end="")
PYEOF
)"
if [[ -z "$key" || "$key" == *$'\n'* || "$key" == *$'\r'* ]]; then
    echo "CONSOLE_EXTERNAL_API_KEY_FILE must contain one non-empty key line." >&2
    exit 2
fi

mkdir -p "$output"
export STYLOMAIL_HOST="$base"
export STYLOMAIL_SMOKE_KEY="$key"
trap 'unset STYLOMAIL_SMOKE_KEY' EXIT INT TERM

echo "== external Host capture =="
echo "base URL: $base"
echo "API key: configured from file (not displayed)"
echo "script: $script"
echo "output: $output"

cd "$REPO"
set +e
dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script "$script" \
    --output "$output"
status=$?
set -e

if [[ ! -f "$output/result.json" ]]; then
    echo "No result.json was written; the external-Host capture did not finish." >&2
    exit 1
fi
if [[ "$status" -ne 0 ]]; then exit "$status"; fi

python3 - "$output/result.json" <<'PYEOF'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    result = json.load(source)
if result.get("success") is not True:
    print("External-Host capture reported failed actions; see the result artifact.", file=sys.stderr)
    raise SystemExit(1)
print("External-Host capture verdict: passed.")
PYEOF

echo "External-Host capture completed; inspect $output/result.json and its screenshots."
