#!/usr/bin/env python3
"""Build an immutable local source snapshot, away from Documents/FileProvider synchronization."""
from pathlib import Path
import shutil,hashlib,json,subprocess,sys,os
root=Path(__file__).resolve().parent.parent
cache=Path.home()/'Library/Caches/DanmakuPlayerBuild'
snapshot=cache/'Source';snapshot.mkdir(parents=True,exist_ok=True)
files=[root/'Package.swift']
for name in ('Sources','Tests','Vendor'):
 files += [p for p in (root/name).rglob('*') if p.is_file() or p.is_symlink()]
manifest=[]
expected=set()
for src in sorted(files):
 relative=src.relative_to(root);dst=snapshot/relative;dst.parent.mkdir(parents=True,exist_ok=True);expected.add(str(relative))
 if src.is_symlink():
  if dst.is_symlink() and os.readlink(dst)==os.readlink(src):continue
  if dst.exists() or dst.is_symlink():dst.unlink()
  dst.symlink_to(os.readlink(src));continue
 digest=hashlib.sha256(src.read_bytes()).hexdigest();manifest.append({'file':str(relative),'sha256':digest})
 if not dst.exists() or hashlib.sha256(dst.read_bytes()).hexdigest()!=digest:shutil.copyfile(src,dst)
for name in ('Sources','Tests','Vendor'):
 for path in (snapshot/name).rglob('*'):
  if (path.is_file() or path.is_symlink()) and str(path.relative_to(snapshot)) not in expected:path.unlink()
record={'sha256':hashlib.sha256(json.dumps(manifest,sort_keys=True).encode()).hexdigest(),'files':manifest,'snapshot':str(snapshot)}
(root/'artifacts').mkdir(exist_ok=True);(root/'artifacts/build-snapshot.json').write_text(json.dumps(record,indent=2))
mode=sys.argv[1] if len(sys.argv)>1 else 'release'
base=['/usr/bin/xcrun','swift']
if mode=='checks':args=['run','--build-system','native','--package-path',str(snapshot),'--scratch-path',str(cache/'SwiftPM'),'CoreChecks']
else:args=['build','--build-system','native','--package-path',str(snapshot),'--scratch-path',str(cache/'SwiftPM'),'-c','release','--product','DanmakuPlayer']
print('Immutable source snapshot:',record['sha256'],flush=True)
subprocess.run(base+args,check=True)
