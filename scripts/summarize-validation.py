#!/usr/bin/env python3
from pathlib import Path
import json,csv,statistics
from acceptance_result import playback_progress
root=Path(__file__).resolve().parent.parent
rows=[]
for path in sorted((root/'artifacts').glob('round-*/application.json')):
 j=json.loads(path.read_text());resource=path.parent/'resources.csv';samples=[]
 if resource.exists():
  with resource.open() as f:
   for line in csv.DictReader(f):
    try:samples.append((float(line['wall_seconds']),float(line['cpu_percent']),float(line['rss_kb'])))
    except (ValueError,KeyError,TypeError):pass
 build_path=path.parent/'build.json'; build=json.loads(build_path.read_text()) if build_path.exists() else {}; requested=build.get('requestedSeconds')
 coverage=requested is not None and bool(samples) and samples[-1][0]>=requested-5 and max((b[0]-a[0] for a,b in zip(samples,samples[1:])),default=0)<=10
 live=playback_progress(j,build.get('formatOnly',False))
 rows.append({'run':path.parent.name,'valid':bool(j.get('complete')) and coverage and live and not any(not x['passed'] for x in j['checks']),'complete':j.get('complete'),'seconds':j.get('elapsed'),'resourceSeconds':samples[-1][0] if samples else None,'passedChecks':sum(bool(x['passed']) for x in j['checks']),'totalChecks':len(j['checks']),'failures':[x for x in j['checks'] if not x['passed']],'cpuMean':statistics.mean(x[1] for x in samples) if samples else None,'rssPeakMB':max(x[2] for x in samples)/1024 if samples else None,'rssStartEndMB':[samples[0][2]/1024,samples[-1][2]/1024] if samples else None})
(root/'artifacts/validation-summary.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2))
print(json.dumps(rows,ensure_ascii=False,indent=2))
