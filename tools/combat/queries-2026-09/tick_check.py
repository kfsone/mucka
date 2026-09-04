# Sanity check: is the combat tick really ~2000ms?
import sqlite3, collections, statistics
d=sqlite3.connect('clogs.db')
rows=d.execute("""select file,enc_idx,npc,ts,kind from ev
   where kind in ('Hit','Miss') and npc is not null order by file,enc_idx,npc,seq""").fetchall()
deltas=[]
prev=None
for f,e,n,ts,k in rows:
    key=(f,e,n)
    if prev and prev[0]==key:
        dt=ts-prev[1]
        if 0<dt<20000: deltas.append(dt)
    prev=(key,ts)
h=collections.Counter(round(x/250)*250 for x in deltas)
print('n deltas',len(deltas))
for b,c in sorted(h.items())[:24]: print(f'{b:6d}ms {c:5d} {"#"*(c//20)}')
print('median', statistics.median(deltas))
