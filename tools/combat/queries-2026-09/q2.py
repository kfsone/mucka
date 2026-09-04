# Q2: is rung-descent RATE stable enough to forecast time-to-kill?
# Rung step = transition from first-observation-of-rung r to first-observation-of-rung r' (r'<r).
# ticks_per_rung_unit = ((ts(r') - ts(r)) / 2000) / (r - r')
import fights, statistics as st, collections
TICK=2000
F=[f for f in fights.load() if f['outcome']=='Kill']
def changepoints(h):
    out=[]
    for ts,r in h:
        if out and out[-1][1]==r: continue
        out.append((ts,r))
    return out
per=collections.defaultdict(list)     # group -> list of ticks_per_rung_unit
halves=collections.defaultdict(lambda:([],[]))
nsteps=collections.defaultdict(list)
allrate=[]
fights_used=collections.Counter()
for f in F:
    cp=changepoints(f['health'])
    steps=[]
    for a,b in zip(cp,cp[1:]):
        dr=a[1]-b[1]
        if dr<=0: continue           # ignore the rare up-steps
        steps.append(((b[0]-a[0])/TICK)/dr)
    if not steps: continue
    g=f['group']; fights_used[g]+=1
    per[g]+=steps; allrate+=steps
    nsteps[g].append(sum(1 for a,b in zip(cp,cp[1:]) if a[1]>b[1]))
    m=len(steps)//2
    if len(steps)>=2:
        halves[g][0].extend(steps[:len(steps)//2] if len(steps)%2==0 else steps[:len(steps)//2])
        halves[g][1].extend(steps[-(len(steps)//2):])
def q(v,p):
    v=sorted(v); import math
    if not v: return float('nan')
    k=(len(v)-1)*p; lo=int(k); hi=min(lo+1,len(v)-1)
    return v[lo]+(v[hi]-v[lo])*(k-lo)
print('KILL fights only. ticks_per_rung_unit (1 tick = 2000ms)\n')
print(f'{"species":16} {"fights":>6} {"steps":>6} {"med":>6} {"IQR":>13} {"med rung steps/fight":>21} {"med rung span":>13}')
rows=[]
for g,v in sorted(per.items(), key=lambda t:-len(t[1])):
    nf=fights_used[g]
    rows.append((g,nf,len(v),st.median(v),q(v,.25),q(v,.75),st.median(nsteps[g])))
for g,nf,ns,md,q1,q3,mns in rows:
    tag='' if nf>=20 else '   (n<20)'
    print(f'{g:16} {nf:6d} {ns:6d} {md:6.1f} {q1:5.1f}-{q3:<6.1f} {mns:21.1f}{tag}')
print(f'\nALL: fights {sum(fights_used.values())}, steps {len(allrate)}, median {st.median(allrate):.1f}, IQR {q(allrate,.25):.1f}-{q(allrate,.75):.1f}')
print('\n--- FIRST HALF vs SECOND HALF of each fight\'s rung-step sequence ---')
print(f'{"species":16} {"n1":>5} {"med1":>6} {"n2":>5} {"med2":>6} {"ratio 2nd/1st":>13}  MWU p')
try:
    from scipy.stats import mannwhitneyu; HAVE=True
except ImportError: HAVE=False
for g,(a,b) in sorted(halves.items(), key=lambda t:-len(t[1][0])):
    if len(a)<10 or len(b)<10: continue
    p=''
    if HAVE: p=f'{mannwhitneyu(a,b,alternative="two-sided").pvalue:.4f}'
    print(f'{g:16} {len(a):5d} {st.median(a):6.1f} {len(b):5d} {st.median(b):6.1f} {st.median(b)/st.median(a):13.2f}  {p}')
A=[x for a,_ in halves.values() for x in a]; B=[x for _,b in halves.values() for x in b]
print(f'{"ALL":16} {len(A):5d} {st.median(A):6.1f} {len(B):5d} {st.median(B):6.1f} {st.median(B)/st.median(A):13.2f}', end=' ')
if HAVE: print(f' {mannwhitneyu(A,B,alternative="two-sided").pvalue:.2e}')
else: print()
print('\n--- rate by STARTING rung of the step (pooled, all species) ---')
byr=collections.defaultdict(list)
for f in F:
    cp=changepoints(f['health'])
    for a,b in zip(cp,cp[1:]):
        dr=a[1]-b[1]
        if dr>0: byr[a[1]].append(((b[0]-a[0])/TICK)/dr)
print(f'{"from rung":>9} {"n":>5} {"median":>7} {"IQR":>13}')
for r in sorted(byr, reverse=True):
    v=byr[r]
    print(f'{r:9d} {len(v):5d} {st.median(v):7.1f} {q(v,.25):5.1f}-{q(v,.75):<6.1f}')
