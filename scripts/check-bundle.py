#!/usr/bin/env python3
from pathlib import Path
import subprocess, json, sys, re, hashlib, plistlib
root=Path(__file__).resolve().parent.parent
app=Path(sys.argv[1]) if len(sys.argv)>1 else Path.home()/'Library/Caches/DanmakuPlayerBuild/DanmakuPlayer.app'
frameworks=app/'Contents/Frameworks'
errors=[];files=[]
for file in [app/'Contents/MacOS/DanmakuPlayer',*frameworks.glob('*.dylib')]:
 output=subprocess.check_output(['otool','-L',str(file)],text=True)
 for line in output.splitlines()[1:]:
  dep=line.strip().split(' (')[0]
  if dep.startswith('/') and not dep.startswith(('/usr/lib/','/System/Library/')):errors.append(f'{file.name}: external {dep}')
  if dep.startswith(('@rpath/','@loader_path/')) and not (frameworks/Path(dep).name).exists():errors.append(f'{file.name}: missing {dep}')
 build=subprocess.check_output(['vtool','-show-build',str(file)],text=True)
 minimum=[line.strip().split()[-1] for line in build.splitlines() if line.strip().startswith('minos')]
 if any(int(m.split('.')[0])>14 for m in minimum): errors.append(f'{file.name}: minos {minimum}')
 commands=subprocess.check_output(['otool','-l',str(file)],text=True)
 rpaths=re.findall(r'\bcmd LC_RPATH\s+cmdsize \d+\s+path (.+?) \(offset',commands)
 for value in rpaths:
  if value.startswith('/') and not value.startswith(('/usr/lib/','/System/Library/')):errors.append(f'{file.name}: external rpath {value}')
 files.append({'file':file.name,'minos':minimum,'rpaths':rpaths})
subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True)
info=plistlib.loads((app/'Contents/Info.plist').read_bytes())
report={'passed':not errors,'app':str(app.resolve()),'binarySHA256':hashlib.sha256((app/'Contents/MacOS/DanmakuPlayer').read_bytes()).hexdigest(),'version':info.get('CFBundleShortVersionString'),'build':info.get('CFBundleVersion'),'errors':errors,'files':files}
(root/'artifacts/bundle-audit.json').write_text(json.dumps(report,indent=2))
print(json.dumps({'passed':not errors,'files':len(files),'errors':errors},indent=2))
raise SystemExit(bool(errors))
