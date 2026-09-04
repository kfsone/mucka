# Q4c: confound check on the one significant cell (broadsword vs axe, zombies).
import sqlite3, os, re, collections, statistics as st
db=sqlite3.connect(os.path.expanduser('~/.mucka/combat/mucka.db'))
wn=lambda w: re.sub(r'\d+$','',w).strip() if w else None
R=[(wn(w),g,h,(lo,hi),s,d,p,ts,enc) for w,g,h,lo,hi,s,d,p,ts,enc in db.execute(
  "select weapon,npc_group,hit,dmg_low,dmg_high,str,dex,persona,ts,encounter_started_at_ms from swings where dir='out'") if w and g]
print('broadsword vs axe on zombies -- str/persona/session breakdown of HITS')
for w in ('broadsword','axe'):
    s=[r for r in R if r[1]=='zombies' and r[0]==w and r[2]]
    strs=[r[4] for r in s if r[4] is not None]
    print(f'\n {w}: hits={len(s)} distinct encounters={len(set(r[8] for r in s))} personas={collections.Counter(r[6] for r in s)}')
    print(f'   str  min/med/max = {min(strs)}/{st.median(strs)}/{max(strs)}   dist={dict(sorted(collections.Counter(r[4]//5*5 for r in s).items()))}')
    by=collections.defaultdict(collections.Counter)
    for r in s: by[r[4]//10*10][r[3]]+=1
    for band in sorted(by):
        c=by[band]; n=sum(c.values())
        print(f'   str {band}-{band+9}: n={n:4d}  20-29={c.get((20,29),0):3d} ({100*c.get((20,29),0)/n:.0f}%)')
print('\n--- str-matched comparison (str 90-99 only) ---')
for w in ('broadsword','axe'):
    s=[r for r in R if r[1]=='zombies' and r[0]==w and r[2] and r[4] is not None and 90<=r[4]<100]
    c=collections.Counter(r[3] for r in s); n=len(s)
    print(f'  {w:11} n={n:4d} '+' '.join(f'{a}-{b}:{100*c.get((a,b),0)/n:.0f}%' for a,b in [(1,4),(5,9),(10,14),(15,19),(20,29)]))
print('\n--- encounter concentration: top encounters per cell ---')
for w in ('broadsword','axe'):
    s=[r for r in R if r[1]=='zombies' and r[0]==w and r[2]]
    c=collections.Counter(r[8] for r in s)
    print(f'  {w:11} top3 encounter shares: {[round(100*v/len(s)) for _,v in c.most_common(3)]}%  of {len(c)} encounters')
    s2=[r for r in s if r[3]==(20,29)]
    print(f'              the {len(s2)} 20-29 hits came from {len(set(r[8] for r in s2))} distinct encounters')
