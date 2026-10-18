"""Run once in an overview-brokered local build/measurement window; no listener or provider calls."""
import datetime
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[3]
project = pathlib.Path(__file__).resolve().parent
out = pathlib.Path(sys.argv[1]).resolve()
out.mkdir(exist_ok=False, parents=True)


def manifest():
    paths = list((root / 'src').rglob('*.cs')) + list((root / 'src').rglob('*.csproj'))
    paths += list(project.iterdir())
    paths += [p for p in root.iterdir() if p.is_file() and
              (p.suffix in ('.props', '.targets') or p.name in ('.editorconfig', 'global.json', 'NuGet.Config', 'nuget.config'))]
    inherited = root.parent / '.editorconfig'
    return {
        'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
        'sdk': subprocess.check_output(['dotnet', '--version'], cwd=root, text=True).strip(),
        'inheritedEditorconfigSha256': hashlib.sha256(inherited.read_bytes()).hexdigest() if inherited.is_file() else None,
        'sources': {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(set(paths)) if p.is_file() and 'obj' not in p.parts and 'bin' not in p.parts},
    }


def census(label):
    rows = subprocess.check_output(['/bin/ps', '-axo', 'pid=,ppid=,comm='], text=True)
    assert rows.strip(), 'Unusable executable census'
    (out / (label + '.txt')).write_text(rows)
    active = []
    for row in rows.splitlines():
        parts = row.strip().split(None, 2)
        assert len(parts) == 3 and parts[0].isdigit(), 'Unusable executable census'
        name = pathlib.Path(parts[2]).name.casefold()
        if name in ('testhost', 'stylomail.host', 'stylomail.proxyharness.reliability', 'stylomail.proxyharness.performance'):
            active.append({'pid': int(parts[0]), 'executable': name})
        if name == 'dotnet':
            probe = subprocess.run(['/bin/ps', '-p', parts[0], '-o', 'args='], capture_output=True, text=True)
            if probe.returncode:
                continue
            args = probe.stdout.split()
            assert len(args) >= 2, 'Unusable dotnet entry census'
            if args[1] in ('build', 'test', 'run', 'publish', 'restore') or any(pathlib.Path(a).name.casefold() in ('testhost.dll', 'vstest.console.dll') for a in args[1:]):
                active.append({'pid': int(parts[0]), 'executable': name})
    assert not active, active


def run(label, command, timeout):
    with (out / (label + '.log')).open('w') as log:
        result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    assert result.returncode == 0, f'{label} exit {result.returncode}; inspect log; no automatic retry'


census('processes-before')
before = manifest()
(out / 'source-before.json').write_text(json.dumps(before, indent=2))
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
run('swap-before', ['/usr/sbin/sysctl', '-n', 'vm.swapusage'], 5)
run('load-before', ['/usr/bin/uptime'], 5)
run('build', ['dotnet', 'build', str(project / 'StyloMail.ProxyHarness.Performance.csproj'),
              '-c', 'Release', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '-warnaserror'], 180)
run('measurement', [str(project / 'bin/Release/net10.0/StyloMail.ProxyHarness.Performance'), str(out / 'measurement')], 180)
after = manifest()
(out / 'source-after.json').write_text(json.dumps(after, indent=2))
assert before == after, 'Source/HEAD/SDK/build-policy changed during take'
census('processes-after')
report = json.loads((out / 'measurement/report.json').read_text())
assert len(report['inputs']) == 3 and len(report['results']) == 21
assert all(row['bytes'] == 1024 * 1024 for row in report['inputs'])
assert all(len(row['elapsedMilliseconds']) == 100 and len(row['allocatedBytes']) == 100 for row in report['results'])
run('swap-after', ['/usr/sbin/sysctl', '-n', 'vm.swapusage'], 5)
run('load-after', ['/usr/bin/uptime'], 5)
(out / 'summary.json').write_text(json.dumps({'complete': True, 'startedUtc': started,
    'completedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'sourceStable': True, 'arms': len(report['results']), 'samplesPerArm': 100}, indent=2))
print(json.dumps({'complete': True, 'arms': 21, 'report': str(out / 'measurement/report.json')}))
