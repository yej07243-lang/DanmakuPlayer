#!/usr/bin/env python3
from pathlib import Path
import shutil,subprocess
root=Path(__file__).resolve().parent.parent;target=root/'artifacts/fixtures';target.mkdir(parents=True,exist_ok=True)
ffmpeg=shutil.which('ffmpeg') or '/opt/homebrew/opt/ffmpeg/bin/ffmpeg'
def ff(*args):subprocess.run([ffmpeg,'-hide_banner','-loglevel','error','-y',*args],check=True)
base=target/'播放测试.2026.S01E01.mkv'
ff('-f','lavfi','-i','testsrc2=size=1920x1080:rate=30','-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-f','lavfi','-i','sine=frequency=880:sample_rate=48000','-t','20','-map','0:v','-map','1:a','-map','2:a','-c:v','libx264','-preset','ultrafast','-crf','28','-c:a','aac',str(base))
ff('-i',str(base),'-map','0:v','-map','0:a:0','-c','copy',str(target/'h264.mp4'))
ff('-i',str(target/'h264.mp4'),'-t','6','-c:v','libx265','-preset','ultrafast','-x265-params','log-level=error','-c:a','aac',str(target/'hevc.mov'))
ff('-i',str(target/'h264.mp4'),'-t','6','-c:v','libvpx-vp9','-deadline','realtime','-cpu-used','8','-b:v','2M','-c:a','libopus',str(target/'vp9.webm'))
ff('-i',str(target/'h264.mp4'),'-t','6','-c:v','mpeg4','-q:v','8','-c:a','mp3',str(target/'mpeg4.avi'))
ff('-f','lavfi','-i','testsrc2=size=3840x2160:rate=30','-t','6','-c:v','libx264','-preset','ultrafast','-crf','30',str(target/'4k.mp4'))
(target/'broken.mkv').write_bytes(b'broken-video')
(target/'stress.xml').write_text('<i>'+''.join(f'<d p="{i/100:.2f},{5 if i%30==0 else 4 if i%31==0 else 1},25,16777215,0,0,a,{i}">压力测试 {i}</d>' for i in range(2000))+'</i>')
print('Fixtures generated. SRT/ASS/NFO fixtures are retained beside them.')
