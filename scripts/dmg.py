#!/usr/bin/env python3
from pathlib import Path
import tempfile, shutil, subprocess, hashlib
root=Path(__file__).resolve().parent.parent
app=Path.home()/'Library/Caches/DanmakuPlayerBuild/DanmakuPlayer.app'
output=root/'dist/DanmakuPlayer-0.1.0-arm64.dmg'
if not app.exists(): raise SystemExit('Run package.py first')
with tempfile.TemporaryDirectory(prefix='danmaku-dmg-') as stage:
 stage=Path(stage); shutil.copytree(app,stage/app.name,symlinks=True)
 (stage/'Applications').symlink_to('/Applications',target_is_directory=True)
 shutil.copy2(root/'docs/INSTALL.md',stage/'使用说明.md')
 shutil.copy2(root/'docs/THIRD_PARTY.md',stage/'THIRD_PARTY.md')
 subprocess.run(['hdiutil','create','-ov','-volname','DanmakuPlayer','-srcfolder',str(stage),'-format','UDZO',str(output)],check=True)
(root/'dist/SHA256SUMS').write_text(hashlib.sha256(output.read_bytes()).hexdigest()+'  '+output.name+'\n')
print(output)
