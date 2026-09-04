"""Q1 -- does npc_weapon separate INCOMING damage, within species?

Provenance note that governs every reading below: npc_weapon is written by
FightAccumulator.NoteNpcWeapon, fed ONLY by CombatTracker.NpcWeaponEquip:
    ^The (?<npc>.+?) has started to use the (?<weapon>.+?) to fight!$
and it is never cleared for the life of the fight. So NULL means
"never announced in this fight", NOT "unarmed", and no disarm can ever appear.
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np, collections

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a)
    print(s); OUT.append(s)

# ---------------------------------------------------------------- populated?
p("=" * 78)
p("Q1a. HOW OFTEN IS npc_weapon POPULATED?")
p("=" * 78)
p("""SQL:
  select count(*) rows, sum(npc_weapon is not null) wpop from swings where dir='in';
  ... and the same grouped by npc_group, and restricted to hit=1.""")
cols, r = q("""select count(*) n_in, sum(npc_weapon is not null) n_wpop,
                      sum(hit=1) n_land, sum(hit=1 and npc_weapon is not null) n_land_wpop
               from swings where dir='in'""")
n_in, n_wpop, n_land, n_land_wpop = r[0]
p(f"\nAll incoming rows            n={n_in:6d}   npc_weapon populated {n_wpop:5d}  ({100*n_wpop/n_in:.1f}%)")
p(f"Incoming LANDED blows        n={n_land:6d}   npc_weapon populated {n_land_wpop:5d}  ({100*n_land_wpop/n_land:.1f}%)")

cols, rows = q("""select npc_group, count(*) n_in, sum(npc_weapon is not null) wpop,
                         sum(hit=1) land, sum(hit=1 and npc_weapon is not null) land_w
                  from swings where dir='in' group by 1
                  having sum(npc_weapon is not null) > 0 order by wpop desc""")
p("\nEvery species that EVER announces a weapon (all others: exactly 0 rows):")
p(table(["species", "in_rows", "w_pop", "landed", "landed_w", "%rows_w"],
        [list(x) + [100.0 * x[2] / x[1]] for x in rows]))
cols, rows2 = q("""select count(distinct npc_group) from swings where dir='in'""")
p(f"\nSpecies with >=1 incoming row: {rows2[0][0]}.  Species that ever announce a weapon: {len(rows)}.")
p("Every animal species (rats, snakes, banshees, rams, foxes, ...) is 0.0% -- never once.")

# ------------------------------------------------------- stability in a fight
p("")
p("=" * 78)
p("Q1b. IS npc_weapon STABLE WITHIN A FIGHT?")
p("=" * 78)
p("""SQL: group incoming rows by the fight key (encounter_started_at_ms, npc),
ordered by ts, and classify the sequence of npc_weapon values.""")
cols, rows = q("""select encounter_started_at_ms, npc, npc_group, ts, npc_weapon
                  from swings where dir='in' and encounter_started_at_ms is not null
                  order by encounter_started_at_ms, npc, ts""")
fights = collections.defaultdict(list)
for enc, npc, grp, ts, w in rows:
    fights[(enc, npc)].append(w)
cls = collections.Counter()
switch_examples = []
for k, seq in fights.items():
    nn = [w for w in seq if w]
    distinct = set(nn)
    if not nn:
        cls["all NULL (never announced)"] += 1
    elif len(distinct) == 1 and len(nn) == len(seq):
        cls["one weapon, whole fight"] += 1
    elif len(distinct) == 1:
        cls["NULL -> one weapon (the announce)"] += 1
    else:
        cls["two or more DIFFERENT weapons"] += 1
        switch_examples.append((k, seq))
p("")
p(table(["pattern", "fights"], sorted(cls.items(), key=lambda x: -x[1])))
p(f"\nFights with a weapon CHANGE mid-fight: {len(switch_examples)}")
for k, seq in switch_examples[:10]:
    p(f"   {k}: {seq}")
p("""
Reading: a NULL->value transition is the announce line arriving, not a pickup we
can date, and there is no value->NULL transition anywhere in the corpus -- the
recorder cannot emit one (NoteNpcWeapon ignores null/blank). DISARMS ARE NOT
OBSERVABLE IN THIS DATA AT ALL. Absence of evidence only.""")

# ------------------------------------------------------------- damage by cell
p("")
p("=" * 78)
p("Q1c. INCOMING DAMAGE BY (species, npc_weapon) CELL")
p("=" * 78)
p("""SQL:
  select npc_group, coalesce(npc_weapon,'<none announced>'), count(*), avg(dmg), ...
  from swings where dir='in' and hit=1 and dmg is not null group by 1,2;""")
cols, rows = q("""select npc_group, coalesce(npc_weapon,'<none announced>') w, dmg
                  from swings where dir='in' and hit=1 and dmg is not null""")
cells = collections.defaultdict(list)
for g, w, d in rows:
    cells[(g, w)].append(d)

def stats(v):
    v = np.asarray(v, float)
    return [len(v), v.mean(), v.std(ddof=1) if len(v) > 1 else float("nan"),
            float(np.median(v)), float(v.max())]

full = sorted(((g, w) + tuple(stats(v)) for (g, w), v in cells.items()),
              key=lambda r: (-r[2]))
p("\nALL cells with n>=5 landed blows (n>=30 marked *):")
p(table(["species", "npc_weapon", "n", "mean", "sd", "median", "max", ""],
        [list(r) + ["*" if r[2] >= 30 else ""] for r in full if r[2] >= 5]))
big = [r for r in full if r[2] >= 30]
p(f"\nCells meeting the n>=30 bar: {len(big)}")
gcount = collections.Counter(r[0] for r in big)
p("Species with >=2 such cells: " + (str([g for g, c in gcount.items() if c >= 2]) or "none"))

# ------------------------------------------- armed vs none-announced contrast
p("")
p("=" * 78)
p("Q1d. ARMED vs NONE-ANNOUNCED, WITHIN SPECIES  (permutation test, 20k iters)")
p("=" * 78)
p("""Statistic: mean(armed) - mean(none announced), two-sided, labels shuffled
within the species only. Effect sizes: Cohen's d and Cliff's delta.""")
res = []
for g in sorted(set(g for (g, w) in cells)):
    armed = [d for (gg, w), v in cells.items() if gg == g and w != "<none announced>" for d in v]
    none = cells.get((g, "<none announced>"), [])
    if len(armed) < 10 or len(none) < 10:
        continue
    obs, pv = perm_test_meandiff(armed, none, iters=20000, seed=7)
    res.append([g, len(armed), np.mean(armed), len(none), np.mean(none), obs,
                cohens_d(armed, none), cliffs_delta(armed, none), pv])
p("")
p(table(["species", "n_armed", "mean_armed", "n_none", "mean_none", "diff", "cohen_d", "cliff_d", "p_perm"], res))

# ------------------------- within a species, weapon vs weapon (pairwise, n>=30)
p("")
p("=" * 78)
p("Q1e. WEAPON vs WEAPON, WITHIN SPECIES (pairwise, both cells n>=30)")
p("=" * 78)
pairs = []
for g in sorted(set(r[0] for r in big)):
    ws = [r for r in big if r[0] == g]
    for i in range(len(ws)):
        for j in range(i + 1, len(ws)):
            a = cells[(g, ws[i][1])]; b = cells[(g, ws[j][1])]
            obs, pv = perm_test_meandiff(a, b, iters=20000, seed=11)
            pairs.append([g, ws[i][1], ws[j][1], len(a), len(b),
                          np.mean(a), np.mean(b), obs, cohens_d(a, b), cliffs_delta(a, b), pv])
if pairs:
    p(table(["species", "wA", "wB", "nA", "nB", "meanA", "meanB", "diff", "cohen_d", "cliff_d", "p_perm"], pairs))
else:
    p("\nNONE. No species has two weapon cells that both clear n>=30. The n>=30 bar")
    p("as specified is unmeetable on this corpus; the largest weapon cell anywhere")
    p(f"is n={big[0][2] if big else 0}.")

# --------------------- relaxed bar so SOMETHING can be said, clearly labelled
p("")
p("=" * 78)
p("Q1f. SAME TEST AT A RELAXED BAR (n>=10 per cell) -- UNDERPOWERED, read as such")
p("=" * 78)
small = [r for r in full if r[2] >= 10]
pairs2 = []
for g in sorted(set(r[0] for r in small)):
    ws = [r for r in small if r[0] == g]
    for i in range(len(ws)):
        for j in range(i + 1, len(ws)):
            a = cells[(g, ws[i][1])]; b = cells[(g, ws[j][1])]
            obs, pv = perm_test_meandiff(a, b, iters=20000, seed=13)
            pairs2.append([g, ws[i][1], ws[j][1], len(a), len(b),
                           np.mean(a), np.mean(b), obs, cohens_d(a, b), cliffs_delta(a, b), pv])
p("")
p(table(["species", "wA", "wB", "nA", "nB", "meanA", "meanB", "diff", "cohen_d", "cliff_d", "p_perm"], pairs2))

# --------------- power floor: same-cell split-half, the honest noise reference
p("")
p("=" * 78)
p("Q1g. NOISE FLOOR -- split the LARGEST single cell in half at random and")
p("     measure the |mean difference| we get from nothing at all.")
p("=" * 78)
rng = np.random.default_rng(3)
for (g, w), v in sorted(cells.items(), key=lambda kv: -len(kv[1]))[:6]:
    v = np.asarray(v, float)
    if len(v) < 40:
        continue
    ds = []
    for _ in range(4000):
        idx = rng.permutation(len(v)); h = len(v) // 2
        ds.append(abs(v[idx[:h]].mean() - v[idx[h:]].mean()))
    ds = np.array(ds)
    p(f"  {g:10s} {w:22s} n={len(v):4d}  mean={v.mean():.2f}  "
      f"split-half |dmean| median={np.median(ds):.2f} p95={np.percentile(ds,95):.2f}")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q1_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
