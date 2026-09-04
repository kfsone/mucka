# Q2e: does a SPECIES PRIOR forecast better than the fight's own observed rate?
import fights, statistics as st, collections
from stats_lite import quant
TICK=2000
F=[f for f in fights.load() if f['outcome']=='Kill']
def cps(h):
    o=[]
    for ts,r in h:
        if o and o[-1][1]==r: continue
        o.append((ts,r))
    return o
prior={}
tmp=collections.defaultdict(list)
for f in F:
    cp=cps(f['health'])
    for a,b in zip(cp,cp[1:]):
        if a[1]>b[1]: tmp[f['group']].append(((b[0]-a[0])/TICK)/(a[1]-b[1]))
prior={g:st.median(v) for g,v in tmp.items() if len(v)>=20}
glob=st.median([x for v in tmp.values() for x in v])
rows=[]
for f in F:
    cp=cps(f['health']); kill=max(e[4] for e in f['ev'] if e[5]=='Kill')
    dn=[(a,b) for a,b in zip(cp,cp[1:]) if a[1]>b[1]]
    if len(dn)<2: continue
    own=st.mean([((b[0]-a[0])/TICK)/(a[1]-b[1]) for a,b in dn[:2]])
    pr=prior.get(f['group'],glob)
    now=dn[1][1]; act=(kill-now[0])/TICK
    if act<0: continue
    rows.append((now[1]*own, now[1]*pr, act))
def score(pred,act):
    rel=[abs(p-a)/max(a,0.5) for p,a in zip(pred,act)]
    good=sum(1 for p,a in zip(pred,act) if abs(p-a)<=0.5*max(a,1))/len(act)
    return st.median(rel), good
A=[r[0] for r in rows]; B=[r[1] for r in rows]; C=[r[2] for r in rows]
print(f'n={len(rows)} kill-fights with >=2 rung steps observed')
print(f'{"forecast source":28} {"med |err|/act":>14} {"within +/-50%":>14}')
r,g=score(A,C); print(f'{"own observed rate (k=2)":28} {r:13.0%} {g:14.0%}')
r,g=score(B,C); print(f'{"species median prior":28} {r:13.0%} {g:14.0%}')
r,g=score([(a+b)/2 for a,b in zip(A,B)],C); print(f'{"50/50 blend":28} {r:13.0%} {g:14.0%}')
r,g=score([st.median(C)]*len(C),C); print(f'{"constant (global median)":28} {r:13.0%} {g:14.0%}')
print('\nspecies medians used as prior (ticks/rung-unit):', {k:round(v,1) for k,v in prior.items()})
