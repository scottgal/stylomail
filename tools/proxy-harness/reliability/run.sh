#!/bin/bash
set -euo pipefail
proxy_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$proxy_repo_root"
artifact_dir="${1:?Pass an artifact directory}"
if (( $# > 3 )); then exit 2; fi
thresholds_path="${2:-tools/proxy-harness/reliability/thresholds.json}"
runner_args=("$artifact_dir" "$thresholds_path")
if (( $# == 3 )); then
    test "$3" = "--exclusive-bind-probe" || exit 2
    runner_args+=("$3")
fi
mkdir -p "$artifact_dir"
test ! -e "$artifact_dir/report.json"
test ! -e "$artifact_dir/thresholds.selected.json"
python3 tools/proxy-harness/reliability/source-manifest.py "$artifact_dir/source-before.json" "$thresholds_path" || exit 2
if ! dotnet build tools/proxy-harness/reliability/StyloMail.ProxyHarness.Reliability.csproj --nologo -m:1 > "$artifact_dir/build.log" 2>&1; then
    cat "$artifact_dir/build.log"
    exit 2
fi
/bin/ps -axo pid=,comm= > "$artifact_dir/processes-before.txt" || exit 2
netstat_status=0
/usr/sbin/netstat -an > "$artifact_dir/sockets-before.txt" 2> "$artifact_dir/sockets-before.stderr.txt" || netstat_status=$?
printf '%s\n' "$netstat_status" > "$artifact_dir/sockets-before.exit.txt"
test -s "$artifact_dir/processes-before.txt" || exit 2
result=0
tools/proxy-harness/reliability/bin/Debug/net10.0/StyloMail.ProxyHarness.Reliability "${runner_args[@]}" > "$artifact_dir/run.log" 2>&1 || result=$?
/bin/ps -axo pid=,comm= > "$artifact_dir/processes-after.txt" || exit 2
netstat_status=0
/usr/sbin/netstat -an > "$artifact_dir/sockets-after.txt" 2> "$artifact_dir/sockets-after.stderr.txt" || netstat_status=$?
printf '%s\n' "$netstat_status" > "$artifact_dir/sockets-after.exit.txt"
python3 tools/proxy-harness/reliability/source-manifest.py "$artifact_dir/source-after.json" "$thresholds_path" || exit 2
test -s "$artifact_dir/processes-after.txt" || exit 2
python3 - "$artifact_dir" <<'PY' || exit 2
import json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
counts = {}
before = json.loads((directory / 'source-before.json').read_text())
after = json.loads((directory / 'source-after.json').read_text())
if before['sources'] != after['sources']:
    raise SystemExit('instrument or proxy sources moved during the run; result invalid')
if before['sdk'] != after['sdk'] or before['inheritedEditorconfigSha256'] != after['inheritedEditorconfigSha256']:
    raise SystemExit('SDK or inherited analyzer policy moved during the run; result invalid')
if before['selectedThresholds'] != after['selectedThresholds']:
    raise SystemExit('selected thresholds moved during the run; result invalid')
selected = directory / 'thresholds.selected.json'
if not selected.is_file():
    raise SystemExit('missing immutable selected-threshold snapshot')
import hashlib
selected_hash = hashlib.sha256(selected.read_bytes()).hexdigest()
if selected_hash != before['selectedThresholds']['sha256']:
    raise SystemExit('executed threshold snapshot does not match source provenance')
report = json.loads((directory / 'report.json').read_text())
if report['thresholdsSha256'].lower() != selected_hash:
    raise SystemExit('reported thresholds do not match the executed snapshot')
table_control = report.get('socketTableControl', {})
table_usable = table_control.get('Status') == 'AVAILABLE' and table_control.get('NativeControlPairSeen') is True
for phase in ('before', 'after'):
    raw = (directory / f'sockets-{phase}.txt').read_text()
    code = int((directory / f'sockets-{phase}.exit.txt').read_text())
    table_usable = table_usable and code == 0 and any(line.startswith('tcp') for line in raw.splitlines())
alternative = report.get('exclusiveBindAlternative', {})
alternative_ok = (alternative.get('available') is True and alternative.get('address') == '127.0.0.1'
                  and alternative.get('ports') == [5271, 5290]
                  and report.get('nativeSocketPositiveControlDelta', 0) >= 2)
(directory / 'socket-table-status.json').write_text(json.dumps({
    'status': 'AVAILABLE' if table_usable else 'UNUSABLE',
    'comparisonAvailable': table_usable,
    'brokeredBindAlternativePassed': alternative_ok,
    'scope': 'Bind proves availability at that instant only; process census and native socket control are separate.'}, indent=2))
if not table_usable and not alternative_ok:
    raise SystemExit('UNUSABLE TCP table and no valid brokered exclusive-bind alternative')
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
