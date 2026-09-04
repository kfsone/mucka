# Q3: how many rung observations do you GET per fight, and when?
import fights, statistics as st, collections
from stats_lite import quant
TICK=2000
ALL=fights.load()
# only fights with real combat (>=1 player swing)
F=[f for f in ALL if f['swings']]
def t0(f):
    c=[e[4] for e in f['ev'] if e[5] in ('FightStart','Hit','Miss')]
    return min(c) if c else min(e[4] for e in f['ev'])
def tend(f): return f['t1']
per=collections.defaultdict(list)
print('=== TABLE Q3a: rung observations per fight (fights with >=1 player swing) ===')
print(f'{"species":14} {"fights":>6} {"%with 0 obs":>11} {"med #obs":>8} {"p25":>4} {"p75":>4} {"med fight len (ticks)":>21}')
buck=collections.Counter()
for f in F: per[f['group']].append(f)
def row(name, fs):
    n=len(fs); z=sum(1 for f in fs if not f['health'])
    obs=[len(f['health']) for f in fs]
    dur=[(tend(f)-t0(f))/TICK for f in fs]
    print(f'{name:14} {n:6d} {100*z/n:10.0f}% {st.median(obs):8.1f} {quant(obs,.25):4.0f} {quant(obs,.75):4.0f} {st.median(dur):21.1f}')
for g in sorted(per, key=lambda g:-len(per[g])):
    if len(per[g])<20: continue
    row(g, per[g])
row('ALL', F)
print()
print('=== TABLE Q3b: ticks from engagement to FIRST rung observation ===')
print('(only fights that got at least one rung line)')
print(f'{"species":14} {"n":>5} {"med":>5} {"p25":>5} {"p75":>5} {"p90":>5} {"med as % of fight":>18} {"%1st obs in tick<=2":>20}')
def row2(name, fs):
    fs=[f for f in fs if f['health']]
    if not fs: return
    d=[(min(t for t,_ in f['health'])-t0(f))/TICK for f in fs]
    frac=[]
    for f in fs:
        L=(tend(f)-t0(f))/TICK
        if L>0: frac.append(((min(t for t,_ in f['health'])-t0(f))/TICK)/L)
    early=sum(1 for x in d if x<=2)/len(d)
    print(f'{name:14} {len(fs):5d} {st.median(d):5.1f} {quant(d,.25):5.1f} {quant(d,.75):5.1f} {quant(d,.90):5.1f} {st.median(frac):17.0%} {early:20.0%}')
for g in sorted(per, key=lambda g:-len(per[g])):
    if len(per[g])<20: continue
    row2(g, per[g])
row2('ALL', F)
print()
print('=== TABLE Q3c: cumulative - by tick T, what % of fights have >=1 rung obs (among fights still alive at T)? ===')
print(f'{"tick":>5} {"fights alive":>12} {"% with >=1 obs":>15} {"% with >=2 obs":>15}')
for T in [0,1,2,3,4,5,6,8,10,15,20,30]:
    alive=[f for f in F if (tend(f)-t0(f))/TICK>=T]
    if len(alive)<20: continue
    a=sum(1 for f in alive if any((t-t0(f))/TICK<=T for t,_ in f['health']))
    b=sum(1 for f in alive if sum(1 for t,_ in f['health'] if (t-t0(f))/TICK<=T)>=2)
    print(f'{T:5d} {len(alive):12d} {100*a/len(alive):14.0f}% {100*b/len(alive):14.0f}%')
print()
print('=== TABLE Q3d: interval between consecutive rung LINES (not rung changes) ===')
iv=[]
for f in F:
    h=sorted(f['health'])
    iv += [(b[0]-a[0])/TICK for a,b in zip(h,h[1:])]
print(f'n={len(iv)} median {st.median(iv):.1f} ticks, IQR {quant(iv,.25):.1f}-{quant(iv,.75):.1f}, p90 {quant(iv,.90):.1f}')
c=collections.Counter(round(x) for x in iv)
for k in sorted(c)[:10]: print(f'  {k:3d} ticks: {c[k]:5d} ({100*c[k]/len(iv):.0f}%)')
