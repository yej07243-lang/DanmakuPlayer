#!/usr/bin/env python3
"""Fetch pinned playback dylibs; never install IINA or change /Applications."""
from pathlib import Path
import urllib.request,hashlib,subprocess,tempfile,shutil,json
root=Path(__file__).resolve().parent.parent
url='https://github.com/iina/iina/releases/download/v1.5.1/IINA.v1.5.1.dmg'
checksum='f414507ec60bcfeda3a9f539f74470253e31b619d031e7a9e9a71519da1598cd'
with tempfile.TemporaryDirectory(prefix='danmaku-deps-') as tmp:
 tmp=Path(tmp); dmg=tmp/'dependencies.dmg'; mount=tmp/'volume'
 print('Downloading pinned dependencies…',flush=True)
 urllib.request.urlretrieve(url,dmg)
 if hashlib.sha256(dmg.read_bytes()).hexdigest()!=checksum: raise SystemExit('Dependency checksum mismatch')
 subprocess.run(['hdiutil','attach',str(dmg),'-nobrowse','-readonly','-mountpoint',str(mount)],check=True)
 try:
  destination=root/'Vendor/lib';destination.mkdir(parents=True,exist_ok=True)
  for file in (mount/'IINA.app/Contents/Frameworks').glob('*.dylib'): shutil.copy2(file,destination/file.name)
  link=destination/'libmpv.dylib'
  if not link.exists(): link.symlink_to('libmpv.2.dylib')
 finally: subprocess.run(['hdiutil','detach',str(mount)],check=True)
headers=root/'Vendor/include/mpv'; headers.mkdir(parents=True,exist_ok=True)
for name in ('client.h','render.h','render_gl.h'):
 urllib.request.urlretrieve('https://raw.githubusercontent.com/mpv-player/mpv/v0.41.0/include/mpv/'+name,headers/name)
(root/'Vendor/provenance.json').write_text(json.dumps({'url':url,'sha256':checksum,'headers':'mpv v0.41.0','note':'Only playback libraries are used. No IINA application code or assets.'},indent=2))
print('Dependencies ready')
