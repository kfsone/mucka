# Q1b: inspect every UP step - real regen or instance reuse / reset?
import fights, sqlite3
d=sqlite3.connect('clogs.db')
F=fights.load()
print(f"{'file':38} {'npc':14} {'from':>4}->{'to':<3} {'dt_ticks':>8}  outcome  context")
n=0
for f in F:
    h=[x for i,x in enumerate(f['health']) if i==0 or x!=f['health'][i-1]]
    for a,b in zip(f['health'],f['health'][1:]):
        if b[1]>a[1]:
            n+=1
            dt=(b[0]-a[0])/2000.0
            # what happened between?
            mid=[e[5] for e in f['ev'] if a[0]<e[4]<=b[0] and e[5] not in ('NpcHealth',)]
            print(f"{f['file']:38} {f['npc']:14} {a[1]:4d}->{b[1]:<3d} {dt:8.1f}  {f['outcome']:<12} {mid[:8]}")
print('total up steps (all fights, incl <3 obs):',n)
