#!/usr/bin/env bash
# Start the embedding service. One instance, loopback only, token from a file.
#
# Run this ON the inference host, inside WSL:
#   bash run.sh                      # foreground, logs to the terminal
#   nohup bash run.sh >> ~/emb2-service.log 2>&1 &   # detached
#
# A second instance is refused by an exclusive lock rather than by a port collision, so a
# duplicate start fails immediately with a named reason instead of after a model load.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV="${EMB_VENV:-$HOME/emb2}"
LOCK="${EMB_LOCK:-$HOME/.emb2-service.lock}"

[ -x "$VENV/bin/python" ] || { echo "REFUSING: no python at $VENV/bin/python" >&2; exit 1; }
[ -f "${EMB_TOKEN_FILE:-$HOME/.config/stylomail/embedding-token}" ] || {
  echo "REFUSING: token file missing; run make-token.sh" >&2; exit 1; }

export EMB_HOST="${EMB_HOST:-127.0.0.1}"
export EMB_PORT="${EMB_PORT:-11440}"
export EMB_MODEL="${EMB_MODEL:-google/embeddinggemma-2}"
export EMB_DIM="${EMB_DIM:-256}"
export EMB_PREFIX="${EMB_PREFIX:-task: classification | query: }"
export EMB_PREFIX_VERSION="${EMB_PREFIX_VERSION:-classification/1}"

exec flock -n "$LOCK" "$VENV/bin/python" "$HERE/embed_app.py"
