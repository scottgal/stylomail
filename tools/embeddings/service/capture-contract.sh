#!/usr/bin/env bash
# Capture two real responses from the running service so the provider can be tested against bytes
# the service actually produced rather than against a hand-written copy of the shape.
#
# Writes to the directory given as the first argument, or /tmp/embed-contract by default. The token
# is read from its file inside python and never printed. No segment text is written to the capture:
# only the response documents, which carry derived values.
#
#   bash capture-contract.sh /tmp/embed-contract
set -euo pipefail

OUT="${1:-/tmp/embed-contract}"
VENV="${EMB_VENV:-$HOME/emb2}"
TOKEN_FILE="${EMB_TOKEN_FILE:-$HOME/.config/stylomail/embedding-token}"
ENDPOINT="${EMB_ENDPOINT:-http://127.0.0.1:11440}"

mkdir -p "$OUT"
exec "$VENV/bin/python" - "$TOKEN_FILE" "$ENDPOINT" "$OUT" <<'PY'
import json
import pathlib
import sys
import urllib.request

token = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").strip()
endpoint = sys.argv[2].rstrip("/")
out = pathlib.Path(sys.argv[3])

def call(path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(endpoint + path, data=data)
    request.add_header("Authorization", "Bearer " + token)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read())

metadata = call("/metadata")
(out / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")

embed = call("/embed", {"segments": [
    "Your account will be suspended unless you confirm your password within 24 hours.",
    "The invoice for the October order is attached for your records.",
]})
(out / "embed.json").write_text(json.dumps(embed, indent=2) + "\n", encoding="utf-8")

print("wrote", out / "metadata.json", out / "embed.json")
print("dimension", len(embed["vectors"][0]), "rows", len(embed["vectors"]))
PY
