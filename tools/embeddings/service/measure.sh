#!/usr/bin/env bash
# Measure the running service the way a caller uses it: over HTTP, with the bearer token, on a
# 200 word segment.
#
# Run this ON the inference host, inside WSL, after starting the service. It never prints the token:
# the value is read from its file inside python and only the status code of an unauthorised probe is
# reported.
#
#   bash measure.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV="${EMB_VENV:-$HOME/emb2}"
TOKEN_FILE="${EMB_TOKEN_FILE:-$HOME/.config/stylomail/embedding-token}"
ENDPOINT="${EMB_ENDPOINT:-http://127.0.0.1:11440}"
RUNS="${EMB_MEASURE_RUNS:-10}"

exec "$VENV/bin/python" - "$TOKEN_FILE" "$ENDPOINT" "$RUNS" <<'PY'
import json
import pathlib
import statistics
import sys
import time
import urllib.error
import urllib.request

token_file = pathlib.Path(sys.argv[1])
endpoint = sys.argv[2].rstrip("/")
runs = int(sys.argv[3])

token = token_file.read_text(encoding="utf-8").strip()
if len(token) < 16:
    raise SystemExit("REFUSING: token file is not a token")

# 200 words, padded so the count is exact and the content is realistic prose rather than one
# repeated token that a tokenizer could compress unrealistically.
base = ("Please review the attached invoice and confirm whether the purchase order number on the "
        "second page matches the one recorded in your system, because the accounts team cannot "
        "release payment until the reference agrees and the delivery window is closing. ")
words = base.split()
while len(words) < 200:
    words += base.split()
text = " ".join(words[:200])
if len(text) > 6000:
    raise SystemExit("REFUSING: the probe segment exceeds the service's own bound")
print(f"segment_words={len(text.split())} segment_chars={len(text)}")


def call(method, path, payload=None, bearer=token):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(endpoint + path, data=data, method=method)
    request.add_header("Authorization", "Bearer " + bearer)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


status, meta = call("GET", "/metadata")
print("metadata_status", status)
print("model_tag", meta.get("model_tag"))
print("model_revision", meta.get("model_revision"))
print("dimension", meta.get("dimension"))
print("native_dimension", meta.get("native_dimension"))
print("prefix_version", meta.get("prefix_version"))
print("dtype", meta.get("dtype"))
print("device", meta.get("device"))
print("max_tokens", meta.get("max_tokens"))

status, body = call("GET", "/metadata", bearer="x" * 32)
print("wrong_token_status", status, "reason", body.get("reason"))

# One untimed warm call, then the measured runs. The first call after a cold start includes graph
# setup on the device and is not a per segment cost.
call("POST", "/embed", {"segments": [text]})
encode_ms = []
wall_ms = []
norms = []
dimensions = set()
first_vector = None
max_drift = 0.0
for _ in range(runs):
    started = time.perf_counter()
    status, body = call("POST", "/embed", {"segments": [text]})
    wall_ms.append((time.perf_counter() - started) * 1000.0)
    if status != 200:
        raise SystemExit(f"REFUSING: embed failed with {status}: {body}")
    vector = body["vectors"][0]
    dimensions.add(len(vector))
    if first_vector is None:
        first_vector = vector
    else:
        max_drift = max(max_drift, max(abs(a - b) for a, b in zip(first_vector, vector)))
    encode_ms.append(body["encode_ms"])
    norms.append(sum(value * value for value in vector) ** 0.5)

print("runs", runs)
print("dimension_returned", sorted(dimensions))
print("norm_min", round(min(norms), 8), "norm_max", round(max(norms), 8))
print("encode_ms_min", round(min(encode_ms), 2))
print("encode_ms_median", round(statistics.median(encode_ms), 2))
print("encode_ms_max", round(max(encode_ms), 2))
print("wall_ms_min", round(min(wall_ms), 2))
print("wall_ms_median", round(statistics.median(wall_ms), 2))
print("wall_ms_max", round(max(wall_ms), 2))
print("repeatability_max_abs_drift", round(max_drift, 9))
PY
