#!/usr/bin/env bash
# Copy the embedding service to the inference host.
#
# The host is Windows with WSL; the service runs inside the Ubuntu distribution, not on Windows.
# Files travel over the ssh connection used for the model host, so there is no second transport to
# configure.
#
# No single quotes are used in the remote command strings on purpose: the remote entry is cmd.exe,
# which does not parse them, so a quoted shell expression arrives at WSL as a literal argument with
# an unmatched quote. Where a shell would be needed, a program is invoked directly instead.
set -euo pipefail

HOST="${EMB_DEPLOY_HOST:-scott@192.168.0.15}"
DEST="${EMB_DEPLOY_PATH:-/home/scott/stylomail-embed}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "== preparing $DEST =="
ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "wsl -d Ubuntu -- mkdir -p $DEST"

echo "== copying $HERE to $HOST:$DEST =="
tar -czf - -C "$HERE" --exclude '__pycache__' --exclude '.pytest_cache' . |
  ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "wsl -d Ubuntu -- tar -xzf - -C $DEST"

echo "== making the launchers executable =="
ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "wsl -d Ubuntu -- chmod 755 $DEST/run.sh $DEST/make-token.sh $DEST/measure.sh"

echo "== deployed files =="
ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "wsl -d Ubuntu -- ls -l $DEST"

echo
echo "Next, on the host (WSL):"
echo "  bash $DEST/make-token.sh                 # once, if no token exists"
echo "  nohup bash $DEST/run.sh >> ~/emb2-service.log 2>&1 &"
