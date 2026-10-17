#!/bin/zsh
# Proves the keep-alive interrupt path removes the Host session and releases all harness ports.
set -euo pipefail
run_log=$(mktemp)
run_dir=".styloagent/artifacts/proxy-e2e-signal-$(date -u +%Y%m%dT%H%M%SZ)"
PROXY_E2E_RUN_DIR="$run_dir" PROXY_E2E_KEEP_ALIVE_SECONDS=30 zsh tools/proxy-harness/run.sh >"$run_log" 2>&1 &
runner=$!
trap 'kill -TERM "$runner" 2>/dev/null || true; wait "$runner" 2>/dev/null || true; rm -f "$run_log"' EXIT
for attempt in {1..40}; do
  if /usr/bin/grep -q '^KEEP-ALIVE ' "$run_log"; then break; fi
  sleep 1
done
/usr/bin/grep -q '^KEEP-ALIVE ' "$run_log"
line=$(/usr/bin/grep '^KEEP-ALIVE ' "$run_log")
host_pid=$(cat "$run_dir/host.pid")
imap_port=$(print -r -- "$line" | sed -nE 's/.* imap-port=([0-9]+).*/\1/p')
smtp_port=$(print -r -- "$line" | sed -nE 's/.* smtp-port=([0-9]+).*/\1/p')
kill -INT "$runner"
wait "$runner" 2>/dev/null || true
if kill -0 "$runner" 2>/dev/null; then
  print -u2 -- 'Harness shell survived SIGINT.'; exit 1
fi
if kill -0 -- "-$host_pid" 2>/dev/null || ps -p "$host_pid" -o pid= | /usr/bin/grep -q '[0-9]'; then
  print -u2 -- 'Host process group survived SIGINT.'; exit 1
fi
python3 - "$imap_port" "$smtp_port" <<'PY'
import errno, socket, sys, time
for port in (5087, *(int(value) for value in sys.argv[1:])):
    for attempt in range(1, 27):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            sock.bind(('127.0.0.1', port))
        except OSError as error:
            if error.errno != errno.EADDRINUSE or attempt == 26:
                raise SystemExit(f"port={port} bind-failed attempt={attempt} errno={error.errno}")
            time.sleep(1)
        else:
            print(f"port={port} bindable-attempt={attempt}")
            break
        finally:
            sock.close()
PY
print -r -- "SIGNAL-TEARDOWN PASS artifacts=$run_dir"
