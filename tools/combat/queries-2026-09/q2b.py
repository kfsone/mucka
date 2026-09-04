# Q2 (final): rung-descent rate; is it constant across the fight?
import fights, statistics as st, collections
from stats_lite import quant, mwu_p, sign_test
TICK=2000
F=[f for f in fights.load() if f['outcome']=='Kill']
def cps(h):
    out=[]
    for ts,r in h:
        if out and out[-1][1]==r: continue
        out.append((ts,r))
    return out
def stepsof(f):
    cp=cps(f['health']); s=[]
    for a,b in zip(cp,cp[1:]):
        dr=a[1]-b[1]
        if dr>0: s.append((((b[0]-a[0])/TICK)/dr, a[1], b[1]))
    return s

per=collections.defaultdict(list); nst=collections.defaultdict(list); fc=collections.Counter()
for f in F:
    s=stepsof(f)
    if not s: continue
    g=f['group']; fc[g]+=1; per[g]+=[x[0] for x in s]; nst[g].append(len(s))
print('=== TABLE Q2a: ticks per rung-unit (1 tick = 2000ms), KILL fights only ===')
print(f'{"species":14} {"fights":>6} {"steps":>6} {"median":>7} {"IQR":>12} {"med steps/fight":>15}')
for g in sorted(per, key=lambda g:-fc[g]):
    if fc[g]<20: continue
    v=per[g]
    print(f'{g:14} {fc[g]:6d} {len(v):6d} {st.median(v):7.1f} {quant(v,.25):4.1f}-{quant(v,.75):<7.1f} {st.median(nst[g]):15.1f}')
print('  -- below n<20 fights, reported but NOT conclusive --')
for g in sorted(per, key=lambda g:-fc[g]):
    if fc[g]>=20 or fc[g]<5: continue
    v=per[g]
    print(f'{g:14} {fc[g]:6d} {len(v):6d} {st.median(v):7.1f} {quant(v,.25):4.1f}-{quant(v,.75):<7.1f} {st.median(nst[g]):15.1f}')
allv=[x for v in per.values() for x in v]
print(f'{"ALL":14} {sum(fc.values()):6d} {len(allv):6d} {st.median(allv):7.1f} {quant(allv,.25):4.1f}-{quant(allv,.75):<7.1f}')

print('\n=== TABLE Q2b: WITHIN-FIGHT first-half vs second-half (paired, removes species mix) ===')
print(f'{"species":14} {"fights>=4steps":>14} {"med 1st":>8} {"med 2nd":>8} {"slower":>7} {"faster":>7} {"sign p":>8}')
byg=collections.defaultdict(list)
for f in F:
    s=[x[0] for x in stepsof(f)]
    if len(s)<4: continue
    h=len(s)//2
    byg[f['group']].append((st.median(s[:h]), st.median(s[-h:])))
tot=[]
for g in sorted(byg, key=lambda g:-len(byg[g])):
    p=byg[g]; tot+=p
    if len(p)<10: continue
    pos,neg,pv=sign_test(p)
    print(f'{g:14} {len(p):14d} {st.median([a for a,_ in p]):8.1f} {st.median([b for _,b in p]):8.1f} {pos:7d} {neg:7d} {pv:8.4f}')
pos,neg,pv=sign_test(tot)
print(f'{"ALL (pooled)":14} {len(tot):14d} {st.median([a for a,_ in tot]):8.1f} {st.median([b for _,b in tot]):8.1f} {pos:7d} {neg:7d} {pv:8.5f}')

print('\n=== TABLE Q2c: rate by starting rung of the step (pooled) ===')
byr=collections.defaultdict(list); byrz=collections.defaultdict(list)
for f in F:
    for r,a,b in stepsof(f):
        byr[a].append(r)
        if f['group'] in ('water-snake','ram','large rat','ogre'): byrz[a].append(r)
print(f'{"from rung":>9} {"n":>5} {"median":>7} {"IQR":>12}   | long-fight species only: n, median')
for r in sorted(byr, reverse=True):
    v=byr[r]; w=byrz.get(r,[])
    ww=f'{len(w):5d} {st.median(w):6.1f}' if w else ''
    print(f'{r:9d} {len(v):5d} {st.median(v):7.1f} {quant(v,.25):4.1f}-{quant(v,.75):<7.1f}   | {ww}')

print('\n=== TABLE Q2d: honesty of a linear forecast ===')
print('After seeing k rung steps, predict remaining ticks = (rung_now - 0) * mean(ticks/rung so far).')
print('Actual = ticks from that observation to the Kill event.')
res=collections.defaultdict(list)
for f in F:
    cp=cps(f['health'])
    kill_ts=max(e[4] for e in f['ev'] if e[5]=='Kill')
    dn=[(a,b) for a,b in zip(cp,cp[1:]) if a[1]>b[1]]
    for k in (1,2,3):
        if len(dn)<k: continue
        rate=st.mean([((b[0]-a[0])/TICK)/(a[1]-b[1]) for a,b in dn[:k]])
        now=dn[k-1][1]
        pred=now[1]*rate
        act=(kill_ts-now[0])/TICK
        if act<0: continue
        res[k].append((pred,act))
print(f'{"k obs":>5} {"n":>5} {"med pred":>9} {"med act":>8} {"med abs err (ticks)":>20} {"med |err|/act":>14} {"within +/-50%":>14}')
for k in sorted(res):
    v=res[k]
    err=[abs(p-a) for p,a in v]
    rel=[abs(p-a)/max(a,0.5) for p,a in v]
    good=sum(1 for p,a in v if abs(p-a)<=0.5*max(a,1))/len(v)
    print(f'{k:5d} {len(v):5d} {st.median([p for p,_ in v]):9.1f} {st.median([a for _,a in v]):8.1f} {st.median(err):20.1f} {st.median(rel):13.0%} {good:14.0%}')
