# Q1e: alternative explanation - is the up-step an artifact of MIXED rung VOCABULARIES?
# MUD2 uses several phrase families (injured / damaged / drained / expiry). If a species mixes
# families mid-fight, a mis-ranked phrase would look like an ascent.
import fights, sqlite3, collections
d=sqlite3.connect('clogs.db')
F=fights.load()
h2p={}
for r in d.execute("select ts,npc,file,rung,phrase from ev where kind='NpcHealth'"):
    h2p[(r[2],r[1],r[0])]=(r[3],r[4])
def fam(p):
    for k in ('injur','damag','drain','expiry','death','fading','weakened','fit','strong','wounds'):
        if k in p: return k
    return '?'
print('UP-STEP phrase pairs:')
mixed=0; same=0
for f in F:
    if len(f['health'])<3: continue
    for a,b in zip(f['health'],f['health'][1:]):
        if b[1]<=a[1]: continue
        pa=h2p.get((f['file'],f['npc'],a[0]),(a[1],'?'))[1]
        pb=h2p.get((f['file'],f['npc'],b[0]),(b[1],'?'))[1]
        m = fam(pa)!=fam(pb)
        mixed+= m; same+= not m
        print(f"  {f['group']:12} {a[1]}'{pa}' -> {b[1]}'{pb}'   {'MIXED-FAMILY' if m else ''}")
print(f'\nup-steps with a phrase-family change: {mixed}; within same family: {same}')
print('\nDoes each species stick to one vocabulary? (phrase families seen per species)')
sp=collections.defaultdict(collections.Counter)
for r in d.execute("select npc,phrase from ev where kind='NpcHealth'"):
    import re
    sp[re.sub(r'\d+$','',r[0]).strip().lower()][fam(r[1])]+=1
for g,c in sorted(sp.items(), key=lambda t:-sum(t[1].values()))[:8]:
    print(f'  {g:14} {dict(c)}')
print('\nFULL rung<->phrase table as parsed (check the ranking is right):')
for r in d.execute("select rung,phrase,count(*) from ev where kind='NpcHealth' group by 1,2 order by 1 desc,3 desc"):
    print(f'  rung {r[0]}  {r[1]:26} n={r[2]}')
