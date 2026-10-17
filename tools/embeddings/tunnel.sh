#!/usr/bin/env bash
# Bring the local embedding service up behind an SSH tunnel, or take it down.
#
# The tunnel is the supervisor, and that is deliberate rather than convenient. The service runs in
# WSL on the inference host, and this host tears down processes started inside a WSL session once the
# last client disconnects: nohup, a lingering systemd --user unit, and a detached tmux server were all
# measured dead within about a minute. An ssh session that runs the service in the foreground and
# forwards the port stays alive for exactly as long as the tunnel does, so "tunnel up" and "service
# up" are the same state and cannot disagree.
#
#   bash tunnel.sh up        # refuse if the local port is bound, start, wait for health
#   bash tunnel.sh status    # is the tunnel process alive, and does the service answer through it
#   bash tunnel.sh down      # stop the tunnel, which stops the service
#
# The bind on the host stays loopback only: the forward target is 127.0.0.1 there.
set -uo pipefail

HOST="${EMB_TUNNEL_HOST:-scott@192.168.0.15}"
LOCAL_PORT="${EMB_TUNNEL_PORT:-11440}"
REMOTE_SERVICE="${EMB_TUNNEL_SERVICE:-/home/scott/stylomail-embed}"
STATE_DIR="${TMPDIR:-/tmp}"
PID_FILE="$STATE_DIR/stylomail-embed-tunnel.pid"
LOG_FILE="$STATE_DIR/stylomail-embed-tunnel.log"

port_bound() {
  python3 - "$LOCAL_PORT" <<'PY'
import socket
import sys
port = int(sys.argv[1])
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
    sock.settimeout(1.0)
    sys.exit(0 if sock.connect_ex(("127.0.0.1", port)) == 0 else 1)
PY
}

health() {
  curl -s -o /dev/null -w '%{http_code}' --max-time 2 "http://127.0.0.1:$LOCAL_PORT/health" 2>/dev/null || true
}

start() {
  if port_bound; then
    echo "REFUSING: something already answers on 127.0.0.1:$LOCAL_PORT; stop it rather than stacking a second tunnel" >&2
    exit 1
  fi

  # A service left running from an earlier attempt holds the same port on the host and would make
  # this session's run.sh refuse its lock. Clearing it is safe because nothing can be using it: the
  # local port was free a moment ago.
  ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" 'wsl -d Ubuntu -- pkill -f embed_app.py' >/dev/null 2>&1 || true
  sleep 1

  : > "$LOG_FILE"
  ssh -o BatchMode=yes -o ConnectTimeout=10 -o ExitOnForwardFailure=yes \
    -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
    -L "127.0.0.1:$LOCAL_PORT:127.0.0.1:11440" \
    "$HOST" "wsl -d Ubuntu -- /bin/bash $REMOTE_SERVICE/run.sh" >> "$LOG_FILE" 2>&1 &
  local pid=$!
  echo "$pid" > "$PID_FILE"

  for _ in $(seq 1 60); do
    if [ "$(health)" = "200" ]; then
      echo "tunnel up on 127.0.0.1:$LOCAL_PORT (ssh pid $pid), service healthy"
      return 0
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "REFUSING: the ssh session exited while the service was starting" >&2
      tail -20 "$LOG_FILE" >&2
      rm -f "$PID_FILE"
      return 1
    fi
    sleep 1
  done

  echo "REFUSING: the service did not become healthy within 60 s" >&2
  tail -20 "$LOG_FILE" >&2
  kill "$pid" 2>/dev/null || true
  rm -f "$PID_FILE"
  return 1
}

case "${1:-}" in
  up)
    start
    ;;
  status)
    pid=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -z "$pid" ]; then echo "no tunnel pid file"; else
      kill -0 "$pid" 2>/dev/null && echo "tunnel process $pid alive" || echo "tunnel process $pid gone"
    fi
    echo "health $(health)"
    ;;
  down)
    pid=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid"
      echo "tunnel $pid stopped"
    else
      echo "no live tunnel to stop"
    fi
    rm -f "$PID_FILE"
    ;;
  *)
    echo "usage: bash tunnel.sh up|status|down" >&2
    exit 2
    ;;
esac
