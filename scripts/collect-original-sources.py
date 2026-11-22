#!/usr/bin/env python3
"""Verify and bundle the original GPL dependencies without including private data."""
from pathlib import Path, PurePosixPath
import argparse, hashlib, json, tarfile, zipfile

ROOT = Path(__file__).resolve().parent.parent

def digest(stream):
    return hashlib.file_digest(stream, 'sha256').hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archives', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    lock = json.loads((ROOT / 'ThirdParty/Dependencies.lock.json').read_text())
    items = lock['components']
    inventory = []
    notices = {}
    for item in items:
        source = args.archives / item['file']
        with source.open('rb') as stream:
            if digest(stream) != item['sha256']:
                raise ValueError('Source checksum mismatch: ' + item['file'])
        names = []
        with tarfile.open(source) as archive:
            for member in archive:
                path = PurePosixPath(*PurePosixPath(member.name).parts[1:])
                if path.is_absolute() or '..' in path.parts:
                    raise ValueError('Unsafe source archive path')
                name = path.name.lower()
                if member.isfile() and (name.startswith(('license', 'copying', 'copyright', 'notice')) or name in ['ftl.txt', 'gplv3']):
                    destination = 'Notices/' + item['name'] + '/' + str(path)
                    notices[destination] = archive.extractfile(member).read()
                    names.append(destination)
        inventory.append({'name': item['name'], 'file': item['file'], 'sha256': item['sha256'], 'notices': names})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix('.part')
    with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_STORED) as archive:
        for item in items:
            archive.write(args.archives / item['file'], 'sources/' + item['file'])
        for path in (ROOT / 'ThirdParty').rglob('*'):
            if path.is_file():
                archive.write(path, str(path.relative_to(ROOT)))
        for name in ['scripts/rebuild-original-deps.py', 'LICENSE', 'COPYRIGHT']:
            archive.write(ROOT / name, name)
        archive.write(ROOT / 'docs/DEPENDENCY_SOURCES.md', 'README.md')
        archive.writestr('SourceInventory.json', json.dumps(inventory, indent=2) + '\n')
        for name, data in notices.items():
            archive.writestr(name, data)
    with zipfile.ZipFile(temporary) as archive:
        if archive.testzip():
            raise ValueError('Source bundle ZIP integrity failure')
        for item in items:
            with archive.open('sources/' + item['file']) as stream:
                if digest(stream) != item['sha256']:
                    raise ValueError('Bundled source checksum mismatch')
    temporary.replace(args.output)
    with args.output.open('rb') as stream:
        result = {'archives': len(items), 'noticeFiles': len(notices), 'sha256': digest(stream), 'file': args.output.name, 'sourceProvenance': lock['sourceInference']}
    print(json.dumps(result, indent=2))

if __name__ == '__main__':
    main()
