"""Record source identity without retaining file contents or credential values."""
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[3]
paths = list((root / 'src').rglob('*.cs'))
paths += list((root / 'src').rglob('*.csproj'))
paths += list(pathlib.Path(__file__).resolve().parent.glob('*.cs'))
paths += list(pathlib.Path(__file__).resolve().parent.glob('*.csproj'))
paths += [path for path in root.iterdir() if path.is_file() and
          (path.suffix in ('.props', '.targets') or path.name in ('.editorconfig', 'global.json', 'NuGet.Config', 'nuget.config'))]
paths += [pathlib.Path(__file__).resolve().parent / 'thresholds.json']
paths += [pathlib.Path(__file__).resolve(), pathlib.Path(__file__).resolve().parent / 'run.sh']
manifest = {
    'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
    'sources': {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(set(paths)) if 'obj' not in path.parts and 'bin' not in path.parts},
    'sdk': subprocess.check_output(['dotnet', '--version'], cwd=root, text=True).strip(),
}
inherited = root.parent / '.editorconfig'
manifest['inheritedEditorconfigSha256'] = hashlib.sha256(inherited.read_bytes()).hexdigest() if inherited.is_file() else None
if len(sys.argv) == 3:
    selected = pathlib.Path(sys.argv[2]).resolve()
    manifest['selectedThresholds'] = {'path': str(selected), 'sha256': hashlib.sha256(selected.read_bytes()).hexdigest()}
pathlib.Path(sys.argv[1]).write_text(json.dumps(manifest, indent=2))
