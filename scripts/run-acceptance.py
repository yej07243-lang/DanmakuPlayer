#!/usr/bin/env python3
"""Run genuine packaged application checks. A report is not a whole release certification."""
from pathlib import Path
import subprocess, os, json, argparse,time,hashlib,fcntl
from acceptance_result import playback_progress
root=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser();parser.add_argument('--round',type=int,required=True);parser.add_argument('--seconds',type=int,default=40);parser.add_argument('--app',type=Path,default=Path.home()/'Library/Caches/DanmakuPlayerBuild/DanmakuPlayer.app');parser.add_argument('--file',type=Path,default=root/'artifacts/fixtures/播放测试.2026.S01E01.mkv')
parser.add_argument('--format-only',action='store_true')
a=parser.parse_args(); folder=root/f'artifacts/round-{a.round}';folder.mkdir(parents=True,exist_ok=True)
lock=(root/'artifacts/acceptance.lock').open('w')
try: fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
except BlockingIOError: raise SystemExit('Another acceptance run is active; do not run two app instances concurrently.')
env=dict(os.environ);env['DANMAKU_DATA_DIR']=str(folder/'data')
if a.seconds>=3600:env['DANMAKU_QA_MUTE']='1'
report=folder/'application.json'
stamp=str(time.time_ns())
for name in ('application.json','build.json','resources.csv','stdout.log'):
 previous=folder/name
 if previous.exists():
  history=folder/'history';history.mkdir(exist_ok=True);previous.replace(history/(stamp+'-'+name))
(folder/'build.json').write_text(json.dumps({'binarySHA256':hashlib.sha256((a.app/'Contents/MacOS/DanmakuPlayer').read_bytes()).hexdigest(),'app':str(a.app),'fixture':str(a.file.resolve()),'requestedSeconds':a.seconds,'formatOnly':a.format_only},ensure_ascii=False,indent=2))
with (folder/'stdout.log').open('w') as log:
 p=subprocess.Popen([str(a.app/'Contents/MacOS/DanmakuPlayer'),'--qa-file',str(a.file.resolve()),'--qa-seconds',str(a.seconds),'--qa-report',str(report)]+(['--qa-format-only'] if a.format_only else []),env=env,stdout=log,stderr=log)
 (folder/'pid').write_text(str(p.pid))
 with (folder/'resources.csv').open('w') as samples:
  samples.write('wall_seconds,cpu_percent,rss_kb\n');started=time.monotonic()
  while p.poll() is None:
   data=subprocess.run(['ps','-p',str(p.pid),'-o','%cpu=,rss='],text=True,capture_output=True).stdout.strip().split()
   if len(data)==2:samples.write(f'{time.monotonic()-started:.3f},{data[0]},{data[1]}\n');samples.flush()
   if time.monotonic()-started>a.seconds+240:
    p.terminate();raise SystemExit('QA timed out; inspect logs')
   time.sleep(2)
 if p.returncode: raise SystemExit(f'Application exited {p.returncode}; inspect {folder}')
if not report.exists():raise SystemExit('Application did not write a report')
j=json.loads(report.read_text());print(json.dumps({'complete':j['complete'],'checks':[(x['name'],x['passed']) for x in j['checks']]},ensure_ascii=False,indent=2))
sample_times=[float(row.split(',')[0]) for row in (folder/'resources.csv').read_text().splitlines()[1:]]
last_sample=sample_times[-1] if sample_times else 0
max_sample_gap=max((b-a for a,b in zip(sample_times,sample_times[1:])),default=0)
coverage=last_sample>=a.seconds-5 and max_sample_gap<=10
live=playback_progress(j,a.format_only)
print(json.dumps({'resourceSampleSeconds':last_sample,'maxResourceGap':max_sample_gap,'resourceCoverage':coverage,'playbackProgress':live},ensure_ascii=False))
raise SystemExit(not j['complete'] or any(not c['passed'] for c in j['checks']) or not coverage or not live)
