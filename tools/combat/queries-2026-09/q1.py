# Q1: Is NPC health rung descent monotonic?
import collections, fights
F=[f for f in fights.load() if len(f['health'])>=3]
def steps(h):
    # dedupe exact-duplicate consecutive observations at identical ts
    out=[]
    for ts,r in h:
        if out and out[-1][0]==ts and out[-1][1]==r: continue
        out.append((ts,r))
    return out
tot=0; nonmono=0; per=collections.defaultdict(lambda:[0,0,0,0]) # fights, nonmono fights, steps, up steps
allsteps=0; upsteps=0; flatsteps=0; downsteps=0
updelta=collections.Counter()
for f in F:
    h=steps(f['health']); 
    if len(h)<3: continue
    tot+=1; g=f['group']; per[g][0]+=1
    up=False
    for a,b in zip(h,h[1:]):
        d=b[1]-a[1]; allsteps+=1; per[g][2]+=1
        if d>0: up=True; upsteps+=1; per[g][3]+=1; updelta[d]+=1
        elif d==0: flatsteps+=1
        else: downsteps+=1
    if up: nonmono+=1; per[g][1]+=1
print(f'fights with >=3 rung obs: {tot}')
print(f'  with any UP step      : {nonmono}  ({100*nonmono/tot:.1f}%)')
print(f'steps total {allsteps}: down {downsteps} ({100*downsteps/allsteps:.1f}%)  flat {flatsteps} ({100*flatsteps/allsteps:.1f}%)  UP {upsteps} ({100*upsteps/allsteps:.1f}%)')
print('up-step magnitudes:', dict(sorted(updelta.items())))
print()
print(f'{"species":22} {"fights":>6} {"nonmono":>7} {"%":>6} {"steps":>6} {"up":>4} {"up%":>6}')
for g,(nf,nm,ns,nu) in sorted(per.items(), key=lambda x:-x[1][0]):
    if nf<5: continue
    print(f'{g:22} {nf:6d} {nm:7d} {100*nm/nf:5.1f}% {ns:6d} {nu:4d} {100*nu/ns:5.1f}%')
print()
print('-- species with <5 fights lumped --')
sm=[0,0,0,0]
for g,v in per.items():
    if v[0]<5: sm=[a+b for a,b in zip(sm,v)]
print(f'{"(other, n<5 each)":22} {sm[0]:6d} {sm[1]:7d} {100*sm[1]/max(sm[0],1):5.1f}% {sm[2]:6d} {sm[3]:4d}')
