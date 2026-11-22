"""Playback evidence gates shared by the runner and report summary."""
import re

def playback_progress(report, format_only=False):
 checks=report.get('checks',[])
 if not format_only:
  return any(c['name']=='continuous-playback' and c['passed'] for c in checks)
 if any(c['name']=='corrupt-file-reported' and c['passed'] for c in checks):
  return True
 # A single decoded frame followed by a stall must not certify playback.
 match=next((re.search(r'duration=([^ ]+)',c.get('details','')) for c in checks if c['name']=='format-playback'),None)
 duration=float(match[1]) if match else 0
 positions=[m.get('position',0) for m in report.get('metrics',[])]
 return duration>0 and max(positions,default=0)>=min(1,duration/2)

if __name__=='__main__':
 # Reproduce the old locked-screen report: all checks true, time stuck at 0.067.
 frozen={'checks':[{'name':'format-playback','passed':True,'details':'duration=20 position=0.067'}],'metrics':[{'position':0},{'position':0.067},{'position':0.067}]}
 assert not playback_progress(frozen,True)
 progressing={**frozen,'metrics':[{'position':0},{'position':1.9},{'position':3.9}]}
 assert playback_progress(progressing,True)
 print('Playback evidence regression checks passed')
