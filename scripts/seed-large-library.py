#!/usr/bin/env python3
from pathlib import Path
import argparse,sqlite3,json,uuid,os
root=Path(__file__).resolve().parent.parent
p=argparse.ArgumentParser();p.add_argument('--round',type=int,default=3);p.add_argument('--count',type=int,default=1000);a=p.parse_args()
source=root/'artifacts/round-1/data/library.sqlite'
if not source.exists():source=root/'artifacts/round-11/data/library.sqlite'
with sqlite3.connect(source) as db: template=json.loads(db.execute('SELECT payload FROM media LIMIT 1').fetchone()[0])
folder=root/f'artifacts/round-{a.round}';data=folder/'data';data.mkdir(parents=True,exist_ok=True)
media=folder/'media';media.mkdir(exist_ok=True)
fixture=root/'artifacts/fixtures/播放测试.2026.S01E01.mkv'
with sqlite3.connect(data/'library.sqlite') as db:
 db.executescript('PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,payload BLOB NOT NULL); CREATE TABLE IF NOT EXISTS roots(path TEXT PRIMARY KEY); PRAGMA user_version=1;')
 for i in range(a.count):
  path=media/f'影片{i:04d}.mkv'
  if not path.exists():os.link(fixture,path)
  item=dict(template);item.update(id=str(uuid.uuid4()),path=str(path),title=f'压力测试影片 {i:04d}',position=0,watched=False,season=None,episode=None,source=None,localDanmaku=None,summary='',calibration={'offset':0,'points':[]})
  existing=db.execute('SELECT id FROM media WHERE path=?',(item['path'],)).fetchone()
  if existing:item['id']=existing[0]
  db.execute('INSERT INTO media(id,path,payload) VALUES(?,?,?) ON CONFLICT(path) DO UPDATE SET payload=excluded.payload',(item['id'],item['path'],json.dumps(item,ensure_ascii=False).encode()))
print(f'Seeded {a.count} playable hard-linked entries; original fixture is unchanged.')
