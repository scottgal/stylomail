#!/usr/bin/env bash
# Create the bearer token file the service and the provider both read.
#
# The token is generated here and written to a 0600 file. It is never printed, never passed on
# a command line and never put in an environment variable, because each of those makes it
# visible in a process listing or a shell history.
#
#   bash make-token.sh              # create if absent, print the path
#   bash make-token.sh --rotate     # replace an existing token
set -euo pipefail

TARGET="${EMB_TOKEN_FILE:-$HOME/.config/stylomail/embedding-token}"
ROTATE=0
[ "${1:-}" = "--rotate" ] && ROTATE=1

if [ -f "$TARGET" ] && [ "$ROTATE" -eq 0 ]; then
  echo "token already present at $TARGET (use --rotate to replace it)"
  exit 0
fi

mkdir -p "$(dirname "$TARGET")"
chmod 700 "$(dirname "$TARGET")"
umask 077
python3 - "$TARGET" <<'PY'
import pathlib, secrets, sys
path = pathlib.Path(sys.argv[1])
path.write_text(secrets.token_urlsafe(48) + "\n", encoding="utf-8")
path.chmod(0o600)
PY

echo "token written to $TARGET (mode 600, value not printed)"
