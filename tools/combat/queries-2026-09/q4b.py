# Q4b: significance tests. (1) does weapon shift the BUCKET histogram within a species?
#      (2) does weapon shift the MISS RATE within a species?  (3) is STR the real driver?
import sqlite3, os, re, collections, random, statistics as st
db=sqlite3.connect(os.path.expanduser('~/.mucka/combat/mucka.db'))
BUCKETS=[(1,4),(5,9),(10,14),(15,19),(20,29),(30,39)]
wn=lambda w: re.sub(r'\d+$','',w).strip() if w else None
rows=[(wn(w),g,hit,(lo,hi),s,d) for w,g,hit,lo,hi,s,d in
      db.execute("select weapon,npc_group,hit,dmg_low,dmg_high,str,dex from swings where dir='out'")
      if w and g]
rnd=random.Random(11)
def perm_chi2(labels, values, N=5000):
    """permutation test: is `values` distribution independent of `labels`?  returns (chi2, p)"""
    cats=sorted(set(values), key=str); labs=sorted(set(labels))
    def chi2(lb):
        tab=collections.Counter(zip(lb,values))
        rt=collections.Counter(lb); ct=collections.Counter(values); n=len(lb); x=0
        for l in labs:
            for c in cats:
                e=rt[l]*ct[c]/n
                if e>0: x+=(tab[(l,c)]-e)**2/e
        return x
    obs=chi2(labels); L=list(labels); c=0
    for _ in range(N):
        rnd.shuffle(L)
        if chi2(L)>=obs-1e-9: c+=1
    return obs,(c+1)/(N+1)

print('=== Q4b-1: within species, does WEAPON change the 5-bucket histogram? (hits only, weapons with >=30 hits) ===')
print(f'{"species":12} {"weapons":>7} {"hits":>6} {"chi2":>8} {"perm p":>8}   weapons')
for g in ['zombies','rats','snakes','banshees','dwarves']:
    sub=[(w,b) for w,gg,h,b,s,d in rows if gg==g and h]
    cnt=collections.Counter(w for w,_ in sub)
    keep={w for w,n in cnt.items() if n>=30}
    sub=[(w,b) for w,b in sub if w in keep]
    if len(keep)<2: continue
    x,p=perm_chi2([w for w,_ in sub],[b for _,b in sub])
    print(f'{g:12} {len(keep):7d} {len(sub):6d} {x:8.1f} {p:8.4f}   {sorted(keep)}')

print('\n=== Q4b-2: within species, does WEAPON change the MISS RATE? (>=30 swings) ===')
print(f'{"species":12} {"weapons":>7} {"swings":>6} {"chi2":>8} {"perm p":>8}   miss% range')
for g in ['zombies','rats','snakes','banshees','dwarves','goblins']:
    sub=[(w,h) for w,gg,h,b,s,d in rows if gg==g]
    cnt=collections.Counter(w for w,_ in sub)
    keep={w for w,n in cnt.items() if n>=30}
    sub=[(w,h) for w,h in sub if w in keep]
    if len(keep)<2: continue
    x,p=perm_chi2([w for w,_ in sub],[h for _,h in sub])
    mr={w:100*sum(1 for a,h in sub if a==w and not h)/cnt[w] for w in keep}
    rng=', '.join(f'{w}:{mr[w]:.0f}' for w in sorted(mr,key=lambda w:mr[w]))
    print(f'{g:12} {len(keep):7d} {len(sub):6d} {x:8.1f} {p:8.4f}   {rng}')

print('\n=== Q4b-3: is STR the driver of the bucket histogram? (pooled, hits only) ===')
print(f'{"str band":>10} {"hits":>6} |'+''.join(f'{f"{a}-{b}":>7}' for a,b in BUCKETS))
bands=[(0,80),(80,90),(90,95),(95,100),(100,999)]
for lo,hi in bands:
    sub=[b for w,g,h,b,s,d in rows if h and s is not None and lo<=s<hi]
    if len(sub)<50: continue
    c=collections.Counter(sub); n=len(sub)
    print(f'{f"{lo}-{hi}":>10} {n:6d} |'+''.join(f'{100*c.get(b,0)/n:6.0f}%' for b in BUCKETS))
sub=[(('%d-%d'%(0 if s<90 else 1,0)),b) for w,g,h,b,s,d in rows if h and s is not None]
x,p=perm_chi2([a for a,_ in sub],[b for _,b in sub])
print(f'  str<90 vs str>=90, buckets: chi2={x:.1f} perm p={p:.4f}  (n={len(sub)})')

print('\n=== Q4b-4: the one visibly-odd cell - broadsword vs axe on zombies, 20-29 bucket ===')
for w in ('broadsword','axe'):
    sub=[b for ww,g,h,b,s,d in rows if g=='zombies' and ww==w and h]
    c=collections.Counter(sub); n=len(sub)
    print(f'  {w:12} hits={n:4d} 20-29 = {c.get((20,29),0):3d} ({100*c.get((20,29),0)/n:.0f}%)')
a=[b==(20,29) for ww,g,h,b,s,d in rows if g=='zombies' and ww=='broadsword' and h]
b_=[b==(20,29) for ww,g,h,b,s,d in rows if g=='zombies' and ww=='axe' and h]
x,p=perm_chi2(['A']*len(a)+['B']*len(b_), a+b_, N=20000)
print(f'  chi2={x:.1f} perm p={p:.5f}   (uncorrected; ~36 weapon pairs tested for zombies -> Bonferroni alpha ~0.0014)')
