# Q4: are outgoing damage buckets separable by weapon, and stable per (weapon, species)?
# Source: ~/.mucka/combat/mucka.db swings (dir='out').
import sqlite3, os, re, collections, statistics as st, random
from stats_lite import quant
db=sqlite3.connect(os.path.expanduser('~/.mucka/combat/mucka.db'))
BUCKETS=[(1,4),(5,9),(10,14),(15,19),(20,29),(30,39)]
def wnorm(w):
    if not w: return None
    return re.sub(r'\d+$','',w).strip()
rows=db.execute("""select weapon, npc_group, hit, dmg_low, dmg_high, str, dex, persona, ts
                   from swings where dir='out'""").fetchall()
print('outgoing swings:', len(rows))
cell=collections.defaultdict(lambda: {'hit':0,'miss':0,'b':collections.Counter(),'str':[],'ts':[]})
for w,g,hit,lo,hi,s,d,p,ts in rows:
    w=wnorm(w)
    if w is None or g is None: continue
    c=cell[(w,g)]
    if hit:
        c['hit']+=1
        c['b'][(lo,hi)]+=1
        if s is not None: c['str'].append(s)
        c['ts'].append(ts)
    else: c['miss']+=1
def pct(c,b):
    n=sum(c['b'].values())
    return 100*c['b'].get(b,0)/n if n else 0
print()
print('=== TABLE Q4a: (weapon x species) cells with >=30 outgoing swings ===')
hdr=f'{"weapon":22} {"species":12} {"swings":>6} {"hits":>5} {"miss%":>6} |'+''.join(f'{f"{a}-{b}":>7}' for a,b in BUCKETS)+f'{"medStr":>7}'
print(hdr); print('-'*len(hdr))
keys=[k for k,c in cell.items() if c['hit']+c['miss']>=30]
keys.sort(key=lambda k:(k[1], -(cell[k]['hit']+cell[k]['miss'])))
for k in keys:
    c=cell[k]; n=c['hit']+c['miss']
    ms=100*c['miss']/n
    line=f'{k[0]:22} {k[1]:12} {n:6d} {c["hit"]:5d} {ms:5.1f}% |'+''.join(f'{pct(c,b):6.0f}%' for b in BUCKETS)
    line+=f'{(st.median(c["str"]) if c["str"] else float("nan")):7.0f}'
    print(line)
print()
print('=== TABLE Q4b: same species, different weapon - does the histogram shift? ===')
print('(total-variation distance between bucket histograms, 0=identical 1=disjoint; only cells with >=30 HITS)')
byg=collections.defaultdict(list)
for k in cell:
    if cell[k]['hit']>=30: byg[k[1]].append(k[0])
def hist(c):
    n=sum(c['b'].values()); return [c['b'].get(b,0)/n for b in BUCKETS]
def tvd(a,b): return 0.5*sum(abs(x-y) for x,y in zip(a,b))
for g,ws in sorted(byg.items(), key=lambda t:-len(t[1])):
    if len(ws)<2: continue
    print(f'\n  {g}:')
    ws=sorted(ws, key=lambda w:-cell[(w,g)]['hit'])
    for i,w1 in enumerate(ws):
        for w2 in ws[i+1:]:
            c1,c2=cell[(w1,g)],cell[(w2,g)]
            print(f'    {w1:20} (h={c1["hit"]:4d}, str~{st.median(c1["str"]):3.0f}) vs {w2:20} (h={c2["hit"]:4d}, str~{st.median(c2["str"]):3.0f})  TVD={tvd(hist(c1),hist(c2)):.3f}')
print()
print('=== TABLE Q4c: split-half stability - how many HITS before the histogram settles? ===')
print('Bootstrap: draw two disjoint samples of size n/2 from a cell, TVD between them. Median over 400 draws.')
print('Compare against the between-weapon TVDs above: a real difference must exceed the noise floor.')
print(f'{"n hits sampled":>15} {"median TVD":>11} {"p90 TVD":>9}  (pooled over all cells with >=60 hits)')
pool=[]
for k,c in cell.items():
    if c['hit']>=60:
        s=[]
        for b,n in c['b'].items(): s+= [b]*n
        pool.append(s)
rnd=random.Random(7)
for n in (10,20,30,40,60,80,120,200,400):
    ds=[]
    for s in pool:
        if len(s)<2*n: continue
        for _ in range(400//max(1,len(pool))+8):
            x=rnd.sample(s,2*n); a,b=x[:n],x[n:]
            ha=[a.count(bk)/n for bk in BUCKETS]; hb=[b.count(bk)/n for bk in BUCKETS]
            ds.append(tvd(ha,hb))
    if len(ds)<20: continue
    print(f'{n:15d} {st.median(ds):11.3f} {quant(ds,.90):9.3f}   (cells contributing: {sum(1 for s in pool if len(s)>=2*n)})')
