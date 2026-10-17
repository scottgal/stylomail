#!/usr/bin/env bash
# Run the service with its output in a log, for a supervisor that has no console to give it.
#
# A Windows-side wsl.exe holds this process for the lifetime of the command it carries, and an ssh
# session that starts wsl.exe directly does not survive the channel closing. This wrapper exists so
# the supervisor can be invoked as an executable plus one path, with no shell quoting to get wrong.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG="${EMB_LOG:-$HOME/emb2-service.log}"
exec bash "$HERE/run.sh" >> "$LOG" 2>&1
