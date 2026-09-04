"""Q4 follow-up. Two loose ends from q4_instance.py.

(1) Key A (npc name) is significant for zombies: F=2.53, ICC=0.035, p=0.012,
    blocked p=0.023. Zombies are also the species that arms itself. Does the
    slot effect survive holding npc_weapon fixed?
(2) At key C the instance IS the fight, and a 30-minute time block is roughly a
    fight, so the blocked permutation has almost nothing left to exchange -- it
    can return p=1.000 by construction rather than by evidence. Quantified here
    so nobody reads those cells as a result.
"""
import sys, os, collections, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

def _ids(labels):
    u = {l: i for i, l in enumerate(dict.fromkeys(labels))}
    return np.array([u[l] for l in labels], dtype=np.int64), len(u)

def F_from_ids(vals, ids, a):
    N = vals.size
    cnt = np.bincount(ids, minlength=a).astype(float)
    tot = np.bincount(ids, weights=vals, minlength=a)
    m = np.divide(tot, cnt, out=np.zeros_like(tot), where=cnt > 0)
    gm = vals.sum() / N
    ssb = float((cnt * (m - gm) ** 2).sum())
    ssw = float(((vals - gm) ** 2).sum()) - ssb
    if a < 2 or N - a < 1 or ssw <= 0:
        return float("nan"), float("nan"), float("nan")
    msb = ssb / (a - 1); msw = ssw / (N - a)
    n0 = (N - (cnt ** 2).sum() / N) / (a - 1)
    vb = max((msb - msw) / n0, 0.0)
    return msb / msw, vb / (vb + msw), float(np.sqrt(vb))

def F_stat(vals, labels):
    v = np.asarray(vals, float); i, a = _ids(list(labels)); return F_from_ids(v, i, a)

def perm_F(vals, labels, iters=4000, seed=17, blocks=None):
    rng = np.random.default_rng(seed)
    v = np.asarray(vals, float); lb0, a = _ids(list(labels))
    obs = F_from_ids(v, lb0, a)[0]; cnt = 0
    if blocks is None:
        lb = lb0.copy()
        for _ in range(iters):
            rng.shuffle(lb)
            cnt += F_from_ids(v, lb, a)[0] >= obs - 1e-12
    else:
        b = np.asarray(blocks)
        ib = [np.where(b == x)[0] for x in np.unique(b)]
        ib = [ix for ix in ib if ix.size > 1]
        for _ in range(iters):
            cur = lb0.copy()
            for ix in ib:
                cur[ix] = cur[rng.permutation(ix)]
            cnt += F_from_ids(v, cur, a)[0] >= obs - 1e-12
    return obs, (cnt + 1) / (iters + 1)

cols, rows = q("""select npc, npc_group, encounter_started_at_ms, ts, dmg, npc_weapon
                  from swings where dir='in' and hit=1 and dmg is not null
                    and encounter_started_at_ms is not null""")
R = [dict(zip(cols, r)) for r in rows]

# ------------------------------------------------------------------ (1)
p("=" * 78)
p("Q4g. THE ZOMBIE SLOT EFFECT, HELD AGAINST npc_weapon")
p("=" * 78)
p("""SQL: the landed-blow set for npc_group='zombies', grouped by npc and by
coalesce(npc_weapon,'<none announced>').""")
Z = [r for r in R if r["npc_group"] == "zombies"]
cnt = collections.Counter(r["npc"] for r in Z)
keep = {k for k, c in cnt.items() if c >= 10}
Zk = [r for r in Z if r["npc"] in keep]
p("\nPer-slot means, and what fraction of that slot's blows came after it armed:")
tab = []
for s in sorted(keep, key=lambda s: -cnt[s]):
    v = [r["dmg"] for r in Zk if r["npc"] == s]
    aw = [r for r in Zk if r["npc"] == s and r["npc_weapon"]]
    tab.append([s, len(v), float(np.mean(v)), float(np.std(v, ddof=1)),
                len(aw) / len(v), ",".join(sorted({r["npc_weapon"] for r in aw})) or "-"])
p("")
p(table(["slot", "blows", "mean", "sd", "frac_armed", "weapons announced"], tab))

# does the slot effect survive within the unarmed-only subset?
for lab, sub in [("<none announced> blows only", [r for r in Zk if not r["npc_weapon"]]),
                 ("armed blows only", [r for r in Zk if r["npc_weapon"]])]:
    c2 = collections.Counter(r["npc"] for r in sub)
    k2 = {k for k, c in c2.items() if c >= 8}
    s2 = [r for r in sub if r["npc"] in k2]
    if len(k2) < 3:
        p(f"\n  {lab}: only {len(k2)} slots at >=8 blows -- not testable")
        continue
    F, icc, sdb = F_stat([r["dmg"] for r in s2], [r["npc"] for r in s2])
    _, pv = perm_F([r["dmg"] for r in s2], [r["npc"] for r in s2], seed=51)
    p(f"\n  {lab}: {len(k2)} slots, {len(s2)} blows -- F={F:.2f} ICC={icc:.3f} "
      f"sd_between={sdb:.3f} p={pv:.3f}")

# and: is slot mean explained by frac_armed?
fa = np.array([t[4] for t in tab]); mu = np.array([t[2] for t in tab])
p(f"\n  correlation(slot mean, slot's fraction of armed blows) over {len(tab)} slots: "
  f"r = {np.corrcoef(fa, mu)[0,1]:+.3f}")
p("""
If the slot effect vanishes once npc_weapon is held fixed, 'zombie4 hits harder'
is 'zombie4 is the one that keeps finding the pick', not a property of the slot.""")

# ------------------------------------------------------------------ (2)
p("")
p("=" * 78)
p("Q4h. THE BLOCKED PERMUTATION AT KEY C IS DEGENERATE -- how degenerate")
p("=" * 78)
p("""At key C the instance is the fight. A 30-minute time block usually contains
one fight, so there is nothing to exchange and the test cannot reject. Counting
how many exchangeable label pairs each design actually has.""")
tab = []
for g in ["rats", "snakes", "zombies", "banshees", "thieves", "rams"]:
    rs = [r for r in R if r["npc_group"] == g]
    for keyname, keyfn, minn in [("A: npc", lambda r: r["npc"], 10),
                                 ("C: npc+fight", lambda r: (r["npc"], r["encounter_started_at_ms"]), 3)]:
        c = collections.Counter(keyfn(r) for r in rs)
        keep2 = {k for k, v in c.items() if v >= minn}
        s = [r for r in rs if keyfn(r) in keep2]
        if len(keep2) < 3 or len(s) < 30:
            continue
        blocks = collections.defaultdict(set)
        for r in s:
            blocks[r["ts"] // (30 * 60 * 1000)].add(keyfn(r))
        multi = sum(1 for b, ls in blocks.items() if len(ls) > 1)
        tab.append([g, keyname, len(keep2), len(s), len(blocks), multi,
                    multi / len(blocks)])
p("")
p(table(["species", "key", "instances", "blows", "time_blocks",
         "blocks with >1 instance", "frac"], tab))
p("""Where 'frac' is near zero the blocked p-value is an artefact of the design and
carries no information. Only the key-A rows should be read from the blocked
column; key-C conclusions rest on the free permutation and on the ICC.""")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q4b_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
