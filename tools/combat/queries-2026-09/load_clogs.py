"""Load ~/.mucka/clogs/*.jsonl into a flat sqlite table `ev` for querying.
Segments events into 'fights': (file, npc-instance) split at terminal events.
Rung scale (from data): 7=fit/strong .. 1=close to death. Descent = decreasing.
"""
import json, os, glob, sqlite3, re, sys

CLOGS = os.path.expanduser('~/.mucka/clogs')
OUT   = sys.argv[1] if len(sys.argv)>1 else 'clogs.db'
if os.path.exists(OUT): os.remove(OUT)
db = sqlite3.connect(OUT)
db.executescript("""
create table ev(
  file text, enc_idx int, ts int, kind text, actor text, npc text, npc_group text,
  weapon text, rlow int, rhigh int, rung int, phrase text, raw text, seq int);
""")

TERMINAL = {'Kill','KilledByNpc','NpcFled','Withdrawn','FightEndOther','YouFled'}

def group_of(npc):
    if not npc: return None
    return re.sub(r'\d+$','', npc).strip().lower()

rows=[]; seq=0
for f in sorted(glob.glob(os.path.join(CLOGS,'*.jsonl'))):
    enc=-1
    base=os.path.basename(f)
    for line in open(f, encoding='utf-8', errors='replace'):
        line=line.strip()
        if not line: continue
        try: o=json.loads(line)
        except Exception: continue
        t=o.get('type')
        if t=='encounter_start': enc+=1; continue
        if t!='event': continue
        seq+=1
        rows.append((base, enc, o.get('ts'), o.get('kind'), o.get('actor'), o.get('npc'),
                     group_of(o.get('npc')), o.get('weapon'), o.get('rangeLow'), o.get('rangeHigh'),
                     o.get('healthRung'), o.get('healthPhrase'), o.get('raw'), seq))
db.executemany('insert into ev values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)', rows)
db.execute('create index i1 on ev(file,enc_idx,npc,seq)')
db.commit()
print('events', len(rows))
print('files', db.execute('select count(distinct file) from ev').fetchone()[0])
for r in db.execute("select kind,count(*) from ev group by 1 order by 2 desc limit 30"): print(r)
