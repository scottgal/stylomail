"""Record source identity without retaining file contents or credential values."""
import hashlib
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[3]
paths = list((root / 'src/StyloMail.AccessProxy').rglob('*.cs'))
paths += list(pathlib.Path(__file__).resolve().parent.glob('*.cs'))
paths += [root / 'src/StyloMail.Host/Proxy' / name for name in
          ('HostProxyBackendTransport.cs', 'HostProxyOptions.cs', 'ProxyTrustAnchors.cs')]
paths += [pathlib.Path(__file__).resolve().parent / 'thresholds.json']
paths += [pathlib.Path(__file__).resolve(), pathlib.Path(__file__).resolve().parent / 'run.sh']
manifest = {
    'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
    'sources': {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
                for path in sorted(paths) if 'obj' not in path.parts and 'bin' not in path.parts},
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(manifest, indent=2))
