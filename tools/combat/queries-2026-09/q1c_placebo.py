"""Q1 placebo. In Q1j, 26 of the 29 announcing fights have n_pre == 1: the
"before" arm is literally the fight's FIRST landed blow and the "after" arm is
everything later. So the test as run cannot tell "it picked up an axe" apart
from "blows later in a fight are bigger than the first one", whatever the cause.

The placebo runs the identical statistic on fights that NEVER announce a weapon.
If the placebo also shows +2, the Q1j result is about fight position, not weapons.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

cols, rows = q("""select encounter_started_at_ms, npc, npc_group, ts, npc_weapon, dmg,
                         time_to_reset, reset_epoch_ms
                  from swings where dir='in' and hit=1 and dmg is not null
                    and encounter_started_at_ms is not null
                  order by encounter_started_at_ms, npc, ts""")
R = [dict(zip(cols, r)) for r in rows]
seq = collections.defaultdict(list)
for r in R:
    seq[(r["encounter_started_at_ms"], r["npc"])].append(r)

def signflip(d, iters=20000, seed=21):
    rng = np.random.default_rng(seed)
    obs = float(np.mean(d))
    cnt = sum(1 for _ in range(iters)
              if abs(float(np.mean(d * rng.choice([-1.0, 1.0], len(d))))) >= abs(obs) - 1e-12)
    return obs, (cnt + 1) / (iters + 1)

p("=" * 78)
p("Q1l. PLACEBO -- 'first landed blow vs the rest', on NON-ANNOUNCING fights")
p("=" * 78)
p("""Same statistic, same >=2-blow requirement, weapon never mentioned. Split by
whether the species is one that can announce a weapon at all.""")
tab = []
for lab, pred in [
        ("announcing fights (Q1j, split AT the announce)",
         lambda rs: bool([r for r in rs if r['npc_weapon']]) and bool([r for r in rs if not r['npc_weapon']])),
        ("non-announcing fights, weapon-capable species",
         lambda rs: not any(r['npc_weapon'] for r in rs)
                    and rs[0]['npc_group'] in {'zombies','thieves','ogres','giants','goblins','skeletons','vampires'}),
        ("non-announcing fights, ALL species",
         lambda rs: not any(r['npc_weapon'] for r in rs)),
        ("non-announcing fights, rats only",
         lambda rs: not any(r['npc_weapon'] for r in rs) and rs[0]['npc_group'] == 'rats')]:
    ds = []
    for k, rs in seq.items():
        if len(rs) < 2 or not pred(rs):
            continue
        if lab.startswith("announcing"):
            pre = [r['dmg'] for r in rs if not r['npc_weapon']]
            post = [r['dmg'] for r in rs if r['npc_weapon']]
        else:
            pre, post = [rs[0]['dmg']], [r['dmg'] for r in rs[1:]]
        if pre and post:
            ds.append(np.mean(post) - np.mean(pre))
    if len(ds) < 5:
        tab.append([lab, len(ds), None, None, None, None]); continue
    d = np.array(ds, float)
    obs, pv = signflip(d)
    tab.append([lab, len(d), obs, float(np.median(d)), float((d > 0).mean()), pv])
p("")
p(table(["cohort", "fights", "mean(post-pre)", "median", "frac>0", "p_signflip"], tab))

p("""
If the non-announcing rows show a comparable positive shift, Q1j measured fight
position and NOT the weapon. Read the two mean columns against each other.""")

# ------------------------------------------------- damage vs blow index, direct
p("")
p("=" * 78)
p("Q1m. INCOMING DAMAGE BY LANDED-BLOW INDEX WITHIN THE FIGHT (all species)")
p("=" * 78)
p("SQL-equivalent: rank landed incoming blows within (encounter, npc) by ts.")
byidx = collections.defaultdict(list)
for k, rs in seq.items():
    for i, r in enumerate(rs, 1):
        byidx[min(i, 8)].append(r['dmg'])
p("")
p(table(["blow_index", "n", "mean_dmg", "sd"],
        [[("8+" if i == 8 else str(i)), len(v), float(np.mean(v)), float(np.std(v, ddof=1))]
         for i, v in sorted(byidx.items())]))
p("(Species mix changes with index -- long fights are the tougher species. This")
p(" is exactly the Simpson's-prone cut; the per-species version follows.)")
for g in ["rats", "zombies", "snakes", "banshees"]:
    bi = collections.defaultdict(list)
    for k, rs in seq.items():
        if rs[0]['npc_group'] != g:
            continue
        for i, r in enumerate(rs, 1):
            bi[min(i, 6)].append(r['dmg'])
    p(f"\n  {g}:")
    p("    " + "  ".join(f"idx{('6+' if i==6 else i)}: n={len(v):4d} mean={np.mean(v):5.2f}"
                          for i, v in sorted(bi.items())))

# ------------------------------------------------ reset_epoch_ms usability
p("")
p("=" * 78)
p("Q1n. IS reset_epoch_ms A USABLE 'same reset' GROUPING KEY?")
p("=" * 78)
cols2, rr = q("""select count(*), count(distinct reset_epoch_ms),
                        count(distinct reset_epoch_ms/60000),
                        count(distinct reset_epoch_ms/300000)
                 from swings where dir='in' and reset_epoch_ms is not null""")
p(f"\nrows={rr[0][0]}  distinct reset_epoch_ms={rr[0][1]}  "
  f"distinct at 1-min rounding={rr[0][2]}  at 5-min rounding={rr[0][3]}")
p("It drifts blow-to-blow (ts + a coarse countdown), so it is NOT constant across")
p("a reset and cannot be used as an instance key. Q4 uses 5-minute rounding as")
p("the reset proxy and reports the sensitivity.")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q1c_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
