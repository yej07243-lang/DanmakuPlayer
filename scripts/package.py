#!/usr/bin/env python3
"""Build a self-contained arm64 local validation app. Public release requires Developer ID notarization."""
from pathlib import Path
import subprocess, shutil, plistlib, hashlib, json, os, sys, re
ROOT=Path(__file__).resolve().parent.parent
os.chdir(ROOT)
if 'DEVELOPER_DIR' not in os.environ:
 for candidate in ('/Applications/Xcode.app/Contents/Developer','/Applications/Xcode-beta.app/Contents/Developer'):
  if (Path(candidate)/'usr/bin/xcrun').exists():
   os.environ['DEVELOPER_DIR']=candidate; break
def run(*args): subprocess.run(args,check=True)
def strip_development_rpaths(path):
 commands=subprocess.check_output(['otool','-l',str(path)],text=True)
 for value in re.findall(r'\bcmd LC_RPATH\s+cmdsize \d+\s+path (.+?) \(offset',commands):
  if value.startswith('/') and not value.startswith(('/usr/lib/','/System/Library/')):
   run('install_name_tool','-delete_rpath',value,str(path))
run(sys.executable,str(ROOT/'scripts/build-local.py'),'release')
binary=Path.home()/'Library/Caches/DanmakuPlayerBuild/SwiftPM/arm64-apple-macosx/release/DanmakuPlayer'
app=Path(os.environ.get('DANMAKU_APP_OUTPUT', str(Path.home()/'Library/Caches/DanmakuPlayerBuild/DanmakuPlayer.app')))
(ROOT/'dist').mkdir(exist_ok=True)
if app.exists(): shutil.rmtree(app)
contents=app/'Contents'
for name in ('MacOS','Frameworks','Resources'): (contents/name).mkdir(parents=True)
shutil.copy2(binary,contents/'MacOS/DanmakuPlayer')
strip_development_rpaths(contents/'MacOS/DanmakuPlayer')
manifest=[]
for path in sorted((Path.home()/'Library/Caches/DanmakuPlayerBuild/Source/Vendor/lib').glob('*.dylib')):
 if path.is_symlink() or path.name=='libswift_Concurrency.dylib': continue
 dest=contents/'Frameworks'/path.name
 # Ship the architecture we actually validate.
 archs=subprocess.check_output(['lipo','-archs',str(path)],text=True).strip().split()
 if len(archs)>1: run('lipo',str(path),'-thin','arm64','-output',str(dest))
 else: shutil.copy2(path,dest)
 manifest.append({'file':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
 # Make every non-system dependency resolve beside its owner; never /opt/homebrew.
 deps=subprocess.check_output(['otool','-L',str(dest)],text=True).splitlines()[1:]
 for dep in deps:
  name=dep.strip().split(' (')[0]
  if name.startswith('@rpath/'):
   run('install_name_tool','-change',name,'@loader_path/'+Path(name).name,str(dest))
 run('install_name_tool','-id','@rpath/'+path.name,str(dest))
 strip_development_rpaths(dest)
 run('codesign','--force','--sign','-',str(dest))
plist={'CFBundleName':'DanmakuPlayer','CFBundleDevelopmentRegion':'zh-Hans','CFBundleLocalizations':['zh-Hans'],'CFBundleDisplayName':'DanmakuPlayer','CFBundleIdentifier':'app.danmaku.player','CFBundleExecutable':'DanmakuPlayer','CFBundlePackageType':'APPL','CFBundleShortVersionString':'0.1.0','CFBundleVersion':'9','LSMinimumSystemVersion':'14.0','NSHighResolutionCapable':True,'CFBundleIconFile':'AppIcon','NSPrincipalClass':'NSApplication','CFBundleDocumentTypes':[{'CFBundleTypeName':'Video','CFBundleTypeRole':'Viewer','LSHandlerRank':'Alternate','CFBundleTypeExtensions':['mp4','mkv','mov','avi','webm','m4v','ts']}]}
(contents/'Info.plist').write_bytes(plistlib.dumps(plist))
for path in (ROOT/'Resources').iterdir():
 if path.is_file(): shutil.copy2(path,contents/'Resources'/path.name)
(contents/'Resources/DependencyManifest.json').write_text(json.dumps(manifest,indent=2))
for file in ('README.md','LICENSE','docs/THIRD_PARTY.md'):
 path=ROOT/file
 if path.exists(): shutil.copy2(path,contents/'Resources'/path.name)
for attribute in ('com.apple.FinderInfo','com.apple.ResourceFork'):
 subprocess.run(['xattr','-dr',attribute,str(app)],stderr=subprocess.DEVNULL)
run('codesign','--force','--sign','-',str(app))
run('codesign','--verify','--deep','--strict',str(app))
print(app)
