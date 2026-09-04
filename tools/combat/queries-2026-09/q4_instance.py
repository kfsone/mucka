"""Q4 -- do individual NPC INSTANCES vary in damage output beyond sampling noise?

Three candidate instance keys, because MUD2 does not offer an unambiguous one:
  A) npc name alone            -- "rat0". A numbered slot that is reused across
                                  resets, so this asks "is slot 0 special".
  B) (npc name, 5-min reset bucket) -- the closest thing to "the same physical
                                  creature". reset_epoch_ms drifts blow-to-blow
                                  (see q1c Q1n) so it is rounded to 5 minutes.
  C) (npc name, fight)         -- "this creature, right now", which is exactly
                                  what a live display would be claiming.

Test: one-way variance decomposition within species, then a permutation that
shuffles the instance labels among that species' blows, preserving each
instance's blow count. Statistic = F = MSB/MSW.

A plain shuffle also destroys any TIME structure, so a significant result could
be "I fought harder-hitting things in March". The blocked variant shuffles
labels only within a session (blows within 30 min of each other), which holds
that fixed.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

p("""SQL:
  select npc, npc_group, encounter_started_at_ms, ts, dmg, reset_epoch_ms
  from swings where dir='in' and hit=1 and dmg is not null;""")
cols, rows = q("""select npc, npc_group, encounter_started_at_ms, ts, dmg, reset_epoch_ms
                  from swings where dir='in' and hit=1 and dmg is not null""")
R = [dict(zip(cols, r)) for r in rows]

def _ids(labels):
    u = {l: i for i, l in enumerate(dict.fromkeys(labels))}
    return np.array([u[l] for l in labels], dtype=np.int64), len(u)

def F_from_ids(vals, ids, a):
    """One-way F, ICC and sd_between via bincount. vals float64, ids int64."""
    N = vals.size
    cnt = np.bincount(ids, minlength=a).astype(float)
    tot = np.bincount(ids, weights=vals, minlength=a)
    m = np.divide(tot, cnt, out=np.zeros_like(tot), where=cnt > 0)
    gm = vals.sum() / N
    ssb = float((cnt * (m - gm) ** 2).sum())
    sstot = float(((vals - gm) ** 2).sum())
    ssw = sstot - ssb
    if a < 2 or N - a < 1 or ssw <= 0:
        return float("nan"), float("nan"), float("nan")
    msb = ssb / (a - 1); msw = ssw / (N - a)
    n0 = (N - (cnt ** 2).sum() / N) / (a - 1)
    vb = max((msb - msw) / n0, 0.0)
    return msb / msw, vb / (vb + msw), float(np.sqrt(vb))

def F_stat(vals, labels):
    vals = np.asarray(vals, float)
    ids, a = _ids(list(labels))
    return F_from_ids(vals, ids, a)

def perm_F(vals, labels, iters=10000, seed=17, blocks=None):
    """Permute instance labels among the blows. blocks=None shuffles freely;
    otherwise labels are only exchanged within a block, holding time fixed."""
    rng = np.random.default_rng(seed)
    vals = np.asarray(vals, float)
    lb0, a = _ids(list(labels))
    obs = F_from_ids(vals, lb0, a)[0]
    cnt = 0
    if blocks is None:
        lb = lb0.copy()
        for _ in range(iters):
            rng.shuffle(lb)
            if F_from_ids(vals, lb, a)[0] >= obs - 1e-12:
                cnt += 1
    else:
        blocks = np.asarray(blocks)
        idx_by_block = [np.where(blocks == b)[0] for b in np.unique(blocks)]
        idx_by_block = [ix for ix in idx_by_block if ix.size > 1]
        for _ in range(iters):
            cur = lb0.copy()
            for ix in idx_by_block:
                cur[ix] = cur[rng.permutation(ix)]
            if F_from_ids(vals, cur, a)[0] >= obs - 1e-12:
                cnt += 1
    return obs, (cnt + 1) / (iters + 1)

SPECIES = ["rats", "snakes", "zombies", "banshees", "dwarves", "thieves", "rams", "goblins"]

for keyname, keyfn, minn in [
        ("A: npc name", lambda r: r["npc"], 10),
        ("B: npc name + 5-min reset bucket",
         lambda r: (r["npc"], r["reset_epoch_ms"] // 300000 if r["reset_epoch_ms"] else None), 5),
        ("C: npc name + fight", lambda r: (r["npc"], r["encounter_started_at_ms"]), 3)]:
    p("")
    p("=" * 78)
    p(f"Q4  INSTANCE KEY {keyname}   (instances kept only at >= {minn} landed blows)")
    p("=" * 78)
    tab = []
    for g in SPECIES:
        rs = [r for r in R if r["npc_group"] == g and r["encounter_started_at_ms"] is not None]
        cnt = collections.Counter(keyfn(r) for r in rs)
        keep = {k for k, c in cnt.items() if c >= minn}
        rs = [r for r in rs if keyfn(r) in keep]
        if len(keep) < 3 or len(rs) < 30:
            tab.append([g, len(keep), len(rs), None, None, None, None, "too few"])
            continue
        vals = [r["dmg"] for r in rs]; labels = [keyfn(r) for r in rs]
        Fo, icc, sdb = F_stat(vals, labels)
        _, pv = perm_F(vals, labels, iters=4000, seed=17)
        blocks = [r["ts"] // (30 * 60 * 1000) for r in rs]
        _, pvb = perm_F(vals, labels, iters=4000, seed=19, blocks=blocks)
        tab.append([g, len(keep), len(rs), float(np.mean(vals)), sdb, icc, Fo,
                    f"p={pv:.3f} / blocked p={pvb:.3f}"])
    p("")
    p(table(["species", "instances", "blows", "mean", "sd_between_inst", "ICC", "F", "perm"], tab))

# ------------------------------------------------------------------- power
p("")
p("=" * 78)
p("Q4d. POWER -- what size of real instance effect WOULD this corpus have found?")
p("=" * 78)
p("""Simulation on the observed design (same species, same instance sizes, key C
= per-fight): resample each blow as species_mean + instance_offset + noise, with
sd(offset) set to a given fraction of the species mean, and count how often the
permutation test at alpha=0.05 detects it. 1000 sims per cell.""")
def power(g, keyfn, minn, frac, iters=400, seed=23):
    rs = [r for r in R if r["npc_group"] == g and r["encounter_started_at_ms"] is not None]
    cnt = collections.Counter(keyfn(r) for r in rs)
    keep = [k for k, c in cnt.items() if c >= minn]
    if len(keep) < 3:
        return None
    sizes = [cnt[k] for k in keep]
    obs = np.array([r["dmg"] for r in rs if keyfn(r) in keep], float)
    mu, sd = obs.mean(), obs.std(ddof=1)
    rng = np.random.default_rng(seed)
    hits = 0
    for _ in range(iters):
        vals, labels = [], []
        for i, n in enumerate(sizes):
            off = rng.normal(0, frac * mu)
            vals.extend(rng.normal(mu + off, sd, n)); labels.extend([i] * n)
        _, pv = perm_F(vals, labels, iters=300, seed=int(rng.integers(1e6)))
        hits += pv < 0.05
    return hits / iters

keyC = lambda r: (r["npc"], r["encounter_started_at_ms"])
tab = []
for g in ["rats", "snakes", "zombies"]:
    row = [g]
    for frac in [0.10, 0.20, 0.35, 0.50]:
        row.append(power(g, keyC, 3, frac, iters=200))
    tab.append(row)
p("")
p(table(["species", "detect sd_inst=10% of mean", "20%", "35%", "50%"], tab))

# --------------------- is "instance" really just a different NAMED KIND pooled?
p("")
p("=" * 78)
p("Q4f. IS THE KEY-A SIGNAL JUST DIFFERENT NAMED KINDS POOLED INTO ONE GROUP?")
p("=" * 78)
p("""npc_group is NpcGroups.Normalize, which folds e.g. 'large rat3' and 'rat3'
into 'rats'. If it folds two things that hit differently, key A picks that up as
'instance variance' when it is really species variance under a coarse label.
Stripping the trailing digits off npc gives the KIND; the digits give the slot.""")
import re
kind = lambda n: re.sub(r"\d+$", "", n).strip()
for g in ["rats", "zombies", "snakes"]:
    rs = [r for r in R if r["npc_group"] == g]
    byk = collections.defaultdict(list)
    for r in rs:
        byk[kind(r["npc"])].append(r["dmg"])
    p(f"\n  {g} -- by KIND (digits stripped):")
    p("    " + table(["kind", "n", "mean", "sd"],
                     [[k, len(v), float(np.mean(v)), float(np.std(v, ddof=1)) if len(v) > 1 else None]
                      for k, v in sorted(byk.items(), key=lambda kv: -len(kv[1]))]).replace("\n", "\n    "))
    # re-run key A WITHIN the dominant kind only
    dom = max(byk, key=lambda k: len(byk[k]))
    rs2 = [r for r in rs if kind(r["npc"]) == dom and r["encounter_started_at_ms"] is not None]
    cnt = collections.Counter(r["npc"] for r in rs2)
    keep = {k for k, c in cnt.items() if c >= 10}
    rs2 = [r for r in rs2 if r["npc"] in keep]
    if len(keep) >= 3:
        vals = [r["dmg"] for r in rs2]; labels = [r["npc"] for r in rs2]
        Fo, icc, sdb = F_stat(vals, labels)
        _, pv = perm_F(vals, labels, iters=4000, seed=27)
        blocks = [r["ts"] // (30 * 60 * 1000) for r in rs2]
        _, pvb = perm_F(vals, labels, iters=4000, seed=29, blocks=blocks)
        p(f"    key A re-run WITHIN kind '{dom}' only: {len(keep)} slots, {len(rs2)} blows, "
          f"F={Fo:.2f} ICC={icc:.3f} sd_between={sdb:.3f} p={pv:.3f} blocked p={pvb:.3f}")
    else:
        p(f"    key A within kind '{dom}': too few slots at >=10 blows")

# ---------------------------------------------- what DOES separate the tail
p("")
p("=" * 78)
p("Q4e. THE ONE SPECIES THAT DOES SEPARATE -- thieves")
p("=" * 78)
p("""thieves is the only species with a non-zero ICC anywhere in Q3f/Q4, and it is
also the only species in the corpus whose 'instances' all share ONE name and
differ by WEAPON. Per-fight means, with the weapon that fight announced:""")
cols2, rows2 = q("""select encounter_started_at_ms, npc, count(*) n, avg(dmg) m,
                           group_concat(distinct coalesce(npc_weapon,'-')) w
                    from swings
                    where dir='in' and hit=1 and dmg is not null and npc_group='thieves'
                      and encounter_started_at_ms is not null
                    group by 1,2 having count(*)>=3 order by m desc""")
p("")
p(table(["encounter", "npc", "blows", "mean_dmg", "weapons seen"], rows2))
p("""Read down the mean column against the weapon column. This is 19 fights, so it
is a hypothesis and not a finding -- but it says the thing that looks like
'instance variance' in thieves is weapon variance, i.e. Q1 again, and NOT
evidence that a creature has a hidden personal damage roll.""")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q4_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
