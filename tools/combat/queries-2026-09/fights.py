"""Shared fight segmentation over clogs.db.
A 'fight' = one NPC instance (file, enc_idx, npc) run of events, closed by a terminal event.
Rung scale from data: 7=fit/strong (best) .. 1=close to death. Descent = DECREASING rung.
Tick = 2000ms (verified empirically: modal Hit/Miss inter-arrival 2000/4000/6000ms).
"""
import sqlite3, re
TICK = 2000
TERMINAL = {'Kill','KilledByNpc','NpcFled','Withdrawn','FightEndOther','YouFled'}
GROUP_OVERRIDE = {}

def group_of(npc):
    return re.sub(r'\d+$','',npc).strip().lower() if npc else None

def load(dbpath='clogs.db'):
    d=sqlite3.connect(dbpath)
    rows=d.execute("""select file,enc_idx,npc,seq,ts,kind,rung,phrase,weapon,rlow,rhigh
                      from ev where npc is not null order by file,enc_idx,npc,seq""").fetchall()
    fights=[]; cur=None; curkey=None
    def flush(outcome):
        nonlocal cur
        if cur and cur['ev']:
            cur['outcome']=outcome
            cur['t0']=min(e[4] for e in cur['ev'])
            cur['t1']=max(e[4] for e in cur['ev'])
            cur['health']=[(e[4],e[6]) for e in cur['ev'] if e[5]=='NpcHealth' and e[6] is not None]
            cur['swings']=[e for e in cur['ev'] if e[5] in ('Hit','Miss')]
            fights.append(cur)
        cur=None
    for r in rows:
        f,e,n,seq,ts,kind = r[0],r[1],r[2],r[3],r[4],r[5]
        key=(f,e,n)
        if key!=curkey:
            flush('open'); curkey=key
        if cur is None:
            cur={'file':f,'enc':e,'npc':n,'group':group_of(n),'ev':[]}
        cur['ev'].append(r)
        if kind in TERMINAL:
            flush(kind)
    flush('open')
    return fights
