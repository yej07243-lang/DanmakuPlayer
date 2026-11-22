#!/usr/bin/env python3
"""Check source-tampering rejection and refusal to package any nonoriginal build."""
from pathlib import Path
import argparse, importlib.util, json, plistlib, subprocess, sys, tempfile

ROOT = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--archives', type=Path, required=True)
args = parser.parse_args()
subprocess.run([sys.executable, str(ROOT / 'scripts/rebuild-original-deps.py'), '--archives', str(args.archives), '--work', '/tmp/unused-danmaku-verification', '--verify-only'], check=True)
lock = json.loads((ROOT / 'ThirdParty/Dependencies.lock.json').read_text())
with tempfile.TemporaryDirectory(prefix='danmaku-source-integrity-') as temporary:
    folder = Path(temporary)
    for item in lock['components']:
        (folder / item['file']).symlink_to((args.archives / item['file']).resolve())
    altered = folder / lock['components'][0]['file']
    altered.unlink(); altered.write_bytes(b'altered source archive')
    result = subprocess.run([sys.executable, str(ROOT / 'scripts/rebuild-original-deps.py'), '--archives', str(folder), '--work', str(folder / 'unused'), '--verify-only'], text=True, capture_output=True)
    assert result.returncode and 'Source checksum mismatch' in result.stderr
    assert not (folder / 'unused').exists()
    app = folder / 'Other.app'
    (app / 'Contents').mkdir(parents=True)
    (app / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleVersion': '10'}))
    output = folder / 'should-not-exist'
    result = subprocess.run([sys.executable, str(ROOT / 'scripts/prepare-original-release.py'), '--app', str(app), '--output', str(output)], text=True, capture_output=True)
    assert result.returncode and not output.exists()
print('Original-release source integrity and build-number guards passed')
