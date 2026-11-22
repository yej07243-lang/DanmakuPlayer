#!/usr/bin/env python3
"""Package the existing build 9 without compiling or changing playback code."""
from pathlib import Path
import argparse, hashlib, json, plistlib, re, shutil, struct, subprocess, tempfile

ROOT = Path(__file__).resolve().parent.parent

def run(*args):
    return subprocess.run([str(arg) for arg in args], check=True, text=True, capture_output=True)

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def text_hash(path):
    data = path.read_bytes()
    if len(data) < 32 or struct.unpack_from('<I', data)[0] != 0xfeedfacf:
        raise ValueError('Expected an arm64 Mach-O: ' + path.name)
    offset = 32
    for _ in range(struct.unpack_from('<I', data, 16)[0]):
        command, length = struct.unpack_from('<II', data, offset)
        if command == 0x19:
            for index in range(struct.unpack_from('<I', data, offset + 64)[0]):
                entry = offset + 72 + index * 80
                section, segment, _, size, position = struct.unpack_from('<16s16sQQI', data, entry)
                if section.rstrip(b'\0') == b'__text' and segment.rstrip(b'\0') == b'__TEXT':
                    return hashlib.sha256(data[position:position + size]).hexdigest()
        offset += length
    raise ValueError('No executable text section: ' + path.name)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=Path.home() / 'Applications/DanmakuPlayer.app')
    parser.add_argument('--output', type=Path, default=Path.home() / 'Library/Caches/DanmakuPlayerOriginalRelease')
    args = parser.parse_args()
    source = args.app.resolve()
    output = args.output.resolve()
    app = output / 'DanmakuPlayer.app'
    if output.exists():
        parser.error('Output must be a new directory; existing packages are preserved')
    info = plistlib.loads((source / 'Contents/Info.plist').read_bytes())
    if info.get('CFBundleVersion') != '9':
        parser.error('Only the original build 9 is authorized for this package')
    identities = dict(re.findall(r'([A-F0-9]{40}) "(Developer ID Application:[^"\n]+)"', run('security', 'find-identity', '-v', '-p', 'codesigning').stdout))
    if len(identities) != 1:
        parser.error('Exactly one installed Developer ID Application identity is required')
    identity, label = next(iter(identities.items()))
    run('codesign', '--verify', '--deep', '--strict', source)
    output.mkdir(parents=True)
    run('ditto', source, app)
    resources = app / 'Contents/Resources'
    for name in ['LICENSE', 'COPYRIGHT', 'README.md', 'OPEN_SOURCE_RELEASE.md', 'docs/THIRD_PARTY.md', 'docs/INSTALL.md']:
        shutil.copy2(ROOT / name, resources / Path(name).name)
    before = {}
    binaries = [app / 'Contents/MacOS/DanmakuPlayer', *sorted((app / 'Contents/Frameworks').glob('*.dylib'))]
    if len(binaries) != 45:
        raise ValueError('Unexpected original dependency count')
    for path in binaries:
        before[path.name] = text_hash(path)
        for dependency in run('otool', '-L', path).stdout.splitlines()[1:]:
            name = dependency.strip().split(' (')[0]
            if name.startswith('/') and not name.startswith(('/usr/lib/', '/System/Library/')):
                raise ValueError('External runtime dependency: ' + name)
        if run('lipo', '-archs', path).stdout.strip() != 'arm64':
            raise ValueError('Only the validated arm64 architecture may be shipped')
    for path in binaries[1:]:
        run('codesign', '--force', '--sign', identity, '--options', 'runtime', '--timestamp', path)
    # The original libmpv embeds LuaJIT, which emits executable memory without MAP_JIT.
    run('codesign', '--force', '--sign', identity, '--options', 'runtime', '--timestamp', '--entitlements', ROOT / 'Resources/Release.entitlements', app)
    run('codesign', '--verify', '--deep', '--strict', app)
    records = []
    for path in binaries:
        signature = run('codesign', '-d', '--verbose=4', path).stderr
        if not all(value in signature for value in ['Authority=Developer ID Application:', 'Timestamp=', '(runtime)']):
            raise ValueError('Incomplete Developer ID signature: ' + path.name)
        if before[path.name] != text_hash(path):
            raise ValueError('Executable code changed: ' + path.name)
        records.append({'file': path.name, 'sha256': sha(path), 'executableTextSHA256': before[path.name]})
    with tempfile.TemporaryDirectory(prefix='danmaku-original-dmg-') as temporary:
        stage = Path(temporary)
        run('ditto', app, stage / app.name)
        (stage / 'Applications').symlink_to('/Applications', target_is_directory=True)
        for name in ['docs/INSTALL.md', 'docs/THIRD_PARTY.md', 'LICENSE', 'COPYRIGHT']:
            shutil.copy2(ROOT / name, stage / Path(name).name)
        dmg = output / 'DanmakuPlayer-0.1.0-build9-macos-arm64.dmg'
        run('hdiutil', 'create', '-volname', 'DanmakuPlayer', '-srcfolder', stage, '-format', 'UDZO', dmg)
    run('codesign', '--force', '--sign', identity, '--timestamp', dmg)
    run('codesign', '--verify', '--strict', dmg)
    run('hdiutil', 'verify', dmg)
    report = {'build': '9', 'app': str(app), 'dmg': str(dmg), 'dmgSHA256': sha(dmg), 'signed': True, 'notarized': False, 'sourceCorrespondenceCleared': False, 'threeFullAcceptanceRounds': False, 'originalExecutableCodeUnchanged': True, 'files': records}
    (output / 'package-report.json').write_text(json.dumps(report, indent=2) + '\n')
    (output / 'SHA256SUMS').write_text(report['dmgSHA256'] + '  ' + dmg.name + '\n')
    print(json.dumps({key: value for key, value in report.items() if key != 'files'}, indent=2))

if __name__ == '__main__':
    main()
