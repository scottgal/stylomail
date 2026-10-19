#!/usr/bin/env bash
# Runs the policy configuration surface against a throwaway Host.
#
# The Host starts with authoritative evaluation off and no pack. One draft is
# seeded through the audited route, so the capture shows a real draft state
# without enabling authoritative evaluation. This is harness data.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=console-harness.sh
source "$HERE/console-harness.sh"

cleanup() {
    console_stop_host
}
trap cleanup EXIT INT TERM

console_runner_run_dir "/tmp/stylomail-console-policy" || exit 2

console_build_all || exit 1

CONSOLE_RESULTS="$CONSOLE_REPO/ux-results/console-policy"
rm -rf "$CONSOLE_RESULTS"
mkdir -p "$CONSOLE_RESULTS"

echo "== starting the throwaway Host on $CONSOLE_BASE =="
console_start_host || exit 1

echo "== seeding one draft through POST /v1/policy/packs =="
HEADERS="$(console_auth_headers)" || exit 1
DRAFT_BODY="$CONSOLE_RUN/data/policy-draft-response.json"
DRAFT_STATUS=$(curl -sS -o "$DRAFT_BODY" -w '%{http_code}' \
    -X POST \
    -H @"$HEADERS" \
    -H "Content-Type: application/json" \
    --data @- \
    "$CONSOLE_BASE/v1/policy/packs" <<'JSON'
{
  "id": "pack",
  "version": "1",
  "context": { "vertical": "Commerce", "audience": "Adults", "jurisdiction": "GB" },
  "categoryLevels": {
    "child_safety_grooming": "Strict",
    "credible_threats_of_violence": "Strict",
    "illegal_content": "Strict",
    "coercion_and_extortion": "Strict",
    "adult_content": "Standard",
    "harassment": "Strict",
    "impersonation": "Standard",
    "credential_request": "Strict",
    "payment_redirection": "Strict",
    "malware_and_attachments": "Strict",
    "promotion": "Standard",
    "unsolicited_mail": "Observe"
  },
  "actionCeiling": "Review",
  "captureText": {
    "mode": "None",
    "maxCharacters": 0,
    "retentionDays": 0,
    "statement": "No message content is retained."
  },
  "evidenceRetentionDays": 30,
  "reviewDeadline": "1.00:00:00",
  "ruleSetVersion": "harness-rules",
  "scorerVersion": "harness-scorer",
  "effectiveFrom": "2026-10-20T00:00:00+00:00",
  "reviewDue": "2027-01-18T00:00:00+00:00",
  "supersedes": null
}
JSON
)

if [[ "$DRAFT_STATUS" != "200" ]]; then
    echo "The draft seed answered $DRAFT_STATUS, so the capture would not show a real draft." >&2
    sed -n '1,4p' "$DRAFT_BODY" >&2 || true
    exit 1
fi

echo "seeded draft pack v1"

console_export_app_env
export ASPNETCORE_URLS=""

echo "== driving the policy configuration page =="
cd "$CONSOLE_REPO" || exit 1

dotnet run --project src/StyloMail.Desktop -- \
    --ux-headless \
    --ux-test \
    --script ux-scripts/console-policy-smoke.yaml \
    --output "$CONSOLE_RESULTS"
STATUS=$?

echo
echo "== result =="
if [[ -f "$CONSOLE_RESULTS/result.json" ]]; then
    cat "$CONSOLE_RESULTS/result.json"
fi
echo
echo "screenshots in $CONSOLE_RESULTS/"

console_final_status "$CONSOLE_RESULTS/result.json" "$STATUS"
exit $?
