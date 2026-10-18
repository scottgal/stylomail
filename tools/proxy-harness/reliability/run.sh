#!/bin/bash
set -euo pipefail
proxy_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$proxy_repo_root"
artifact_dir="${1:?Pass an artifact directory}"
mkdir -p "$artifact_dir"
test ! -e "$artifact_dir/report.json"
python3 tools/proxy-harness/reliability/source-manifest.py "$artifact_dir/source-before.json" || exit 2
if ! dotnet build tools/proxy-harness/reliability/StyloMail.ProxyHarness.Reliability.csproj --nologo -m:1 > "$artifact_dir/build.log" 2>&1; then
    cat "$artifact_dir/build.log"
    exit 2
fi
/bin/ps -axo pid=,comm= > "$artifact_dir/processes-before.txt" || exit 2
/usr/sbin/netstat -an > "$artifact_dir/sockets-before.txt" || exit 2
test -s "$artifact_dir/processes-before.txt" || exit 2
test -s "$artifact_dir/sockets-before.txt" || exit 2
result=0
tools/proxy-harness/reliability/bin/Debug/net10.0/StyloMail.ProxyHarness.Reliability "$artifact_dir" > "$artifact_dir/run.log" 2>&1 || result=$?
/bin/ps -axo pid=,comm= > "$artifact_dir/processes-after.txt" || exit 2
/usr/sbin/netstat -an > "$artifact_dir/sockets-after.txt" || exit 2
python3 tools/proxy-harness/reliability/source-manifest.py "$artifact_dir/source-after.json" || exit 2
test -s "$artifact_dir/processes-after.txt" || exit 2
test -s "$artifact_dir/sockets-after.txt" || exit 2
python3 - "$artifact_dir" <<'PY' || exit 2
import json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
counts = {}
before = json.loads((directory / 'source-before.json').read_text())
after = json.loads((directory / 'source-after.json').read_text())
if before['sources'] != after['sources']:
    raise SystemExit('instrument or proxy sources moved during the run; result invalid')
for phase in ('before', 'after'):
    rows = (directory / f'processes-{phase}.txt').read_text().splitlines()
    if not rows:
        raise SystemExit('unusable process census')
    count = 0
    for row in rows:
        parts = row.strip().split(None, 1)
        if len(parts) != 2 or not parts[0].isdigit():
            raise SystemExit('unusable ps comm row')
        if pathlib.Path(parts[1]).name.casefold() == 'StyloMail.ProxyHarness.Reliability'.casefold():
            count += 1
    counts[phase] = count
(directory / 'process-teardown.json').write_text(json.dumps(counts, indent=2))
if counts != dict(before=0, after=0):
    raise SystemExit('harness executable census did not return to zero')
PY
cat "$artifact_dir/run.log"
if (( result > 1 )); then exit 2; fi
exit "$result"
