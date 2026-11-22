#!/usr/bin/env python3
"""Rebuild the original GPL dependency configuration from the accompanying sources."""
from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess, tarfile

ROOT = Path(__file__).resolve().parent.parent

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archives', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    lock = json.loads((ROOT / 'ThirdParty/Dependencies.lock.json').read_text())
    options = json.loads((ROOT / 'ThirdParty/build-options.json').read_text())
    pins = {item['name']: item for item in lock['components']}
    for item in pins.values():
        if digest(args.archives / item['file']) != item['sha256']:
            raise ValueError('Source checksum mismatch: ' + item['file'])
    if args.verify_only:
        assert len(options) == len(pins) == 30
        assert {item['source'] for item in options} == set(pins)
        print('Verified 30 source archives and 30 component build specifications')
        return
    base = args.work.resolve()
    if base.exists():
        parser.error('Use a new work directory; existing builds are never deleted')
    prefix = base / 'install/arm64'
    prefix.mkdir(parents=True)
    sdk = subprocess.check_output(['xcrun', '--show-sdk-path'], text=True).strip()
    flags = '-arch arm64 -mmacosx-version-min=11.0 -isysroot ' + sdk
    env = dict(os.environ, MACOSX_DEPLOYMENT_TARGET='11.0', CC='clang', CXX='clang++', CFLAGS=flags + ' -O2', CXXFLAGS=flags + ' -O2', OBJCFLAGS=flags + ' -O2', LDFLAGS=flags, PKG_CONFIG_LIBDIR=str(prefix / 'lib/pkgconfig'), PKG_CONFIG_PATH=str(prefix / 'lib/pkgconfig'))
    substitutions = {'prefix': str(prefix), 'arch': 'arm64', 'MACOSX_DEPLOYMENT_TARGET': '11.0', 'HOST_TRIPLE': 'aarch64-apple-darwin', 'CFLAGS': env['CFLAGS'], 'LDFLAGS': flags}

    def expand(value):
        for key, replacement in substitutions.items():
            value = value.replace('${' + key + '}', replacement).replace('$' + key, replacement)
        if '$' in value:
            raise ValueError('Unresolved build option: ' + value)
        return value

    records = []
    for item in options:
        name, pin = item['name'], pins[item['source']]
        unpack = base / 'sources' / name
        unpack.mkdir(parents=True)
        with tarfile.open(args.archives / pin['file']) as archive:
            archive.extractall(unpack, filter='data')
        entries = list(unpack.iterdir())
        if len(entries) != 1 or not entries[0].is_dir():
            raise ValueError('Unexpected archive root: ' + name)
        source = entries[0]
        for patch in sorted((ROOT / 'ThirdParty/patches' / name).glob('*.patch')):
            subprocess.run(['patch', '-p1', '-i', str(patch.resolve())], cwd=source, check=True)
        build = base / 'build' / name
        build.mkdir(parents=True)
        extra = [expand(value) for value in item['options']]
        if item['system'] == 'configure':
            commands = [[str(source / 'configure'), *extra], ['make', '-j8'], ['make', 'install']]
        elif item['system'] == 'meson':
            commands = [['meson', 'setup', str(build), str(source), '--prefix=' + str(prefix), '--libdir=lib', '--buildtype=release', '--default-library=shared', *extra], ['ninja', '-j8'], ['ninja', 'install']]
        elif item['system'] == 'cmake':
            commands = [['cmake', '-S', str(source / item['sourceSubdirectory']), '-B', str(build), '-DCMAKE_INSTALL_PREFIX=' + str(prefix), '-DCMAKE_PREFIX_PATH=' + str(prefix), '-DCMAKE_OSX_ARCHITECTURES=arm64', '-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0', '-DCMAKE_OSX_SYSROOT=' + sdk, '-DCMAKE_BUILD_TYPE=Release', '-DBUILD_SHARED_LIBS=ON', '-DCMAKE_FIND_ROOT_PATH=' + str(prefix) + ';' + sdk, '-DCMAKE_FIND_ROOT_PATH_MODE_LIBRARY=ONLY', '-DCMAKE_FIND_ROOT_PATH_MODE_INCLUDE=ONLY', '-DCMAKE_FIND_ROOT_PATH_MODE_PACKAGE=ONLY', '-DCMAKE_FIND_FRAMEWORK=LAST', *extra], ['cmake', '--build', str(build), '-j8'], ['cmake', '--install', str(build)]]
        elif name == 'luajit':
            build = source
            commands = [['make', '-j8', 'MACOSX_DEPLOYMENT_TARGET=11.0', 'XCFLAGS=-arch arm64 -mmacosx-version-min=11.0', 'PREFIX=' + str(prefix), 'DEFAULT_CC=clang', 'amalg'], ['make', 'install', 'PREFIX=' + str(prefix)]]
        elif name == 'mujs':
            build = source
            commands = [['make', '-j8', 'CC=clang', 'CFLAGS=' + env['CFLAGS'], 'LDFLAGS=' + flags, 'prefix=' + str(prefix), 'VERSION=' + pin['version'], 'release'], ['make', 'prefix=' + str(prefix), 'install'], ['make', 'prefix=' + str(prefix), 'install-shared']]
        else:
            raise ValueError('Unknown build system: ' + name)
        print('Building ' + name, flush=True)
        with (base / (name + '.log')).open('w') as log:
            for command in commands:
                log.write(json.dumps(command) + '\n'); log.flush()
                subprocess.run(command, cwd=build, env=env, stdout=log, stderr=log, check=True)
        records.append({'name': name, 'commands': commands, 'sourceSHA256': pin['sha256']})
        (base / 'build-record.json').write_text(json.dumps(records, indent=2) + '\n')
    print('Dependencies installed at ' + str(prefix))

if __name__ == '__main__':
    main()
