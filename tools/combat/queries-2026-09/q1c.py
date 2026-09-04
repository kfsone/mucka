# Q1c: separate instance-collision artifacts from candidate real regen.
# Collision risk = npc name has NO numeric suffix AND >1 concurrent same-named NPC in that encounter.
import fights, sqlite3, re, collections
d=sqlite3.connect('clogs.db')
F=fights.load()
# how many distinct FightStart / Kill for the bare name in the same file+enc?
cnt=collections.Counter()
for r in d.execute("select file,enc_idx,npc,kind from ev where kind in ('FightStart','Kill')"):
    cnt[(r[0],r[1],r[2],r[3])]+=1
def collision(f):
    if re.search(r'\d$', f['npc']): return False   # indexed => disambiguated
    return cnt[(f['file'],f['enc'],f['npc'],'FightStart')]>1 or cnt[(f['file'],f['enc'],f['npc'],'Kill')]>1
tot=0; nm=0; nm_coll=0; nm_clean=0; steps=0; up=0; up_coll=0
per=collections.defaultdict(lambda:[0,0])
for f in F:
    if len(f['health'])<3: continue
    tot+=1; g=f['group']; per[g][0]+=1
    u=[(a,b) for a,b in zip(f['health'],f['health'][1:]) if b[1]>a[1]]
    steps+=len(f['health'])-1; up+=len(u)
    if u:
        nm+=1
        if collision(f): nm_coll+=1; up_coll+=len(u)
        else: nm_clean+=1; per[g][1]+=1
print(f'fights >=3 obs: {tot}; any-up: {nm}; of those, same-name-collision-risk: {nm_coll} ({up_coll} up steps); clean: {nm_clean}')
print(f'up steps total {up}; after removing collision-risk fights: {up-up_coll} / {steps} steps = {100*(up-up_coll)/steps:.2f}%')
print()
print('CLEAN non-monotonic rate by species (collision fights excluded from numerator):')
print(f'{"species":22} {"fights":>6} {"nonmono":>7} {"%":>7}')
for g,(nf,x) in sorted(per.items(), key=lambda t:-t[1][0]):
    if nf<5: continue
    print(f'{g:22} {nf:6d} {x:7d} {100*x/nf:6.1f}%')
# Fisher-ish: zombie vs non-zombie
z=per['zombie']; oz=[sum(v[0] for k,v in per.items() if k!='zombie'), sum(v[1] for k,v in per.items() if k!='zombie')]
print(f'\nzombie {z[1]}/{z[0]} = {100*z[1]/z[0]:.1f}%   non-zombie {oz[1]}/{oz[0]} = {100*oz[1]/oz[0]:.1f}%')
try:
    from scipy.stats import fisher_exact
    print('fisher p =', fisher_exact([[z[1],z[0]-z[1]],[oz[1],oz[0]-oz[1]]])[1])
except ImportError:
    # exact binomial permutation
    import random
    obs=z[1]; pool=[1]*(z[1]+oz[1])+[0]*((z[0]-z[1])+(oz[0]-oz[1])); hits=0; N=200000
    for _ in range(N):
        hits += 1 if sum(random.sample(pool,z[0]))>=obs else 0
    print(f'permutation p(one-sided, zombie>=obs) = {hits/N:.4f}  (N={N})')
