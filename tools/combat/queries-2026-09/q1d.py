# Q1d: confound test - do zombie up-steps coincide with a Kill of ANOTHER same-group NPC
# shortly before (which would let the client renumber instances)?
import fights, sqlite3, collections
d=sqlite3.connect('clogs.db')
F=fights.load()
# index all Kill events by (file,enc) -> list of (ts,npc)
kills=collections.defaultdict(list)
for r in d.execute("select file,enc_idx,ts,npc from ev where kind='Kill'"):
    kills[(r[0],r[1])].append((r[2],r[3]))
# how many distinct instances of the same group in the encounter
inst=collections.defaultdict(set)
for r in d.execute("select file,enc_idx,npc from ev where npc is not null"):
    import re
    inst[(r[0],r[1],re.sub(r'\d+$','',r[2]).strip().lower())].add(r[2])

print(f"{'file':34} {'npc':10} {'step':>8} {'#inst':>5}  kill_of_other_within_prev_30s")
withkill=0; tot=0
for f in F:
    if len(f['health'])<3: continue
    for a,b in zip(f['health'],f['health'][1:]):
        if b[1]<=a[1]: continue
        tot+=1
        ni=len(inst[(f['file'],f['enc'],f['group'])])
        ko=[n for ts,n in kills[(f['file'],f['enc'])] if n!=f['npc'] and a[0]-30000<=ts<=b[0]]
        if ko: withkill+=1
        print(f"{f['file'][:34]:34} {f['npc']:10} {a[1]}->{b[1]:<5} {ni:5d}  {ko}")
print(f'\nup-steps={tot}, with another-instance Kill in prior 30s = {withkill}')
# baseline: what fraction of ALL steps have such a kill nearby?
tt=0; ww=0
for f in F:
    if len(f['health'])<3: continue
    for a,b in zip(f['health'],f['health'][1:]):
        tt+=1
        if [n for ts,n in kills[(f['file'],f['enc'])] if n!=f['npc'] and a[0]-30000<=ts<=b[0]]: ww+=1
print(f'baseline over all steps: {ww}/{tt} = {100*ww/tt:.1f}%')
# multi-instance exposure baseline
mi_f=sum(1 for f in F if len(f['health'])>=3 and len(inst[(f['file'],f['enc'],f['group'])])>1)
mi_z=sum(1 for f in F if len(f['health'])>=3 and f['group']=='zombie' and len(inst[(f['file'],f['enc'],f['group'])])>1)
zf=sum(1 for f in F if len(f['health'])>=3 and f['group']=='zombie')
print(f'\nfights >=3obs in multi-instance rooms: {mi_f}; zombie fights {zf}, of which multi-instance {mi_z}')
# non-mono rate: multi-instance vs single-instance, non-zombie only
for label,pred in [('NONzombie single',lambda f: f['group']!='zombie' and len(inst[(f['file'],f['enc'],f['group'])])==1),
                   ('NONzombie multi', lambda f: f['group']!='zombie' and len(inst[(f['file'],f['enc'],f['group'])])>1),
                   ('zombie single',   lambda f: f['group']=='zombie' and len(inst[(f['file'],f['enc'],f['group'])])==1),
                   ('zombie multi',    lambda f: f['group']=='zombie' and len(inst[(f['file'],f['enc'],f['group'])])>1)]:
    s=[f for f in F if len(f['health'])>=3 and pred(f)]
    nm=sum(1 for f in s if any(b[1]>a[1] for a,b in zip(f['health'],f['health'][1:])))
    print(f'{label:18} fights {len(s):4d}  nonmono {nm:3d}  {100*nm/max(len(s),1):5.1f}%')
