"""Q3 -- how noisy is a CURRENT-FIGHT incoming-damage estimate, and when (if
ever) does it beat the species prior?

The prior is LEAVE-ONE-FIGHT-OUT: the species mean over every landed incoming
blow in the corpus EXCEPT this fight's. A live client would not have the current
fight in its baseline (SwingDamageIndex already excludes it), and leaving it in
would rig the comparison in the prior's favour.

Two targets, both reported:
  (a) the fight's OWN final mean  -- all its landed blows, first k included
  (b) the REST of the fight       -- blows k+1..end only
(b) is the one that answers the design question, because it is the only target
the k-blow estimate is not part of.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

p("""SQL:
  select encounter_started_at_ms, npc, npc_group, ts, dmg
  from swings where dir='in' and hit=1 and dmg is not null
    and encounter_started_at_ms is not null
  order by encounter_started_at_ms, npc, ts;""")
cols, rows = q("""select encounter_started_at_ms, npc, npc_group, ts, dmg
                  from swings where dir='in' and hit=1 and dmg is not null
                    and encounter_started_at_ms is not null
                  order by encounter_started_at_ms, npc, ts""")
fights = collections.defaultdict(list)
grp = {}
for enc, npc, g, ts, d in rows:
    fights[(enc, npc)].append(float(d)); grp[(enc, npc)] = g
sp_sum = collections.defaultdict(float); sp_n = collections.defaultdict(int)
for k, v in fights.items():
    sp_sum[grp[k]] += sum(v); sp_n[grp[k]] += len(v)

def prior_loo(k):
    g = grp[k]; v = fights[k]
    n = sp_n[g] - len(v)
    return (sp_sum[g] - sum(v)) / n if n >= 20 else None

p("")
p("=" * 78)
p("Q3a. COHORT SIZES -- the >=8-blow cohort asked for is very thin")
p("=" * 78)
tab = []
for thr in [3, 4, 5, 6, 8, 10]:
    sel = [k for k, v in fights.items() if len(v) >= thr]
    c = collections.Counter(grp[k] for k in sel)
    tab.append([thr, len(sel), sum(len(fights[k]) for k in sel),
                ", ".join(f"{a}:{b}" for a, b in c.most_common(5))])
p("")
p(table([">=N landed blows", "fights", "blows", "top species"], tab))
p("""Only 39 fights in the whole corpus reach 8 landed incoming blows, and a third
of those are rats. Everything at k=5 and k=8 below is therefore reported but
should not be leaned on; the k=1..3 rows at the >=4 bar are where the evidence is.""")

def run(minblows, ks, seed=41, label=""):
    sel = [k for k, v in fights.items() if len(v) >= minblows and prior_loo(k) is not None]
    p("")
    p("=" * 78)
    p(f"Q3{label}. COHORT: fights with >={minblows} landed incoming blows  (n={len(sel)} fights)")
    p("=" * 78)
    out = []
    for K in ks:
        rec = []
        for key in sel:
            v = fights[key]
            if len(v) <= K:
                continue
            g = grp[key]; pr = prior_loo(key); sm = sp_sum[g] / sp_n[g]
            cur = float(np.mean(v[:K]))
            final = float(np.mean(v))
            rest = float(np.mean(v[K:]))
            rec.append((g, sm, cur, pr, final, rest))
        if len(rec) < 5:
            continue
        sm = np.array([r[1] for r in rec]); cur = np.array([r[2] for r in rec])
        pr = np.array([r[3] for r in rec]); fin = np.array([r[4] for r in rec])
        rest = np.array([r[5] for r in rec])
        ec_f = np.abs(cur - fin) / sm
        ep_f = np.abs(pr - fin) / sm
        ec_r = np.abs(cur - rest) / sm
        ep_r = np.abs(pr - rest) / sm
        out.append([K, len(rec),
                    float(np.median(ec_f)), float(np.median(ep_f)),
                    float(np.median(ec_r)), float(np.median(ep_r)),
                    float((ec_r < ep_r).mean()),
                    float(np.mean((cur - rest) ** 2) ** .5 / sm.mean()),
                    float(np.mean((pr - rest) ** 2) ** .5 / sm.mean())])
    p("")
    p(table(["k", "fights", "MAE_cur/own_final", "MAE_prior/own_final",
             "MAE_cur/REST", "MAE_prior/REST", "cur beats prior (frac)",
             "RMSE_cur/REST", "RMSE_prior/REST"], out))
    p("  (all MAE/RMSE columns are medians resp. RMS of |error| divided by the species mean)")
    return sel, out

sel4, out4 = run(4, [1, 2, 3, 5, 8], label="b")
sel8, out8 = run(8, [1, 2, 3, 5, 8], label="c")

# ------------------------------------------------------------- shrinkage
p("")
p("=" * 78)
p("Q3d. SHRINKAGE -- is a BLEND better than either? (>=4-blow cohort)")
p("=" * 78)
p("""blend = (k*current_mean + m*prior) / (k + m).  m = 0 is pure current-fight,
m = inf is pure prior. Scored as RMSE against the REST of the fight, in units of
the species mean. The m that minimises RMSE is the number of blows' worth of
evidence the prior is actually worth.""")
sel = [k for k, v in fights.items() if len(v) >= 4 and prior_loo(k) is not None]
tab = []
for K in [1, 2, 3, 5]:
    rec = [(sp_sum[grp[k]] / sp_n[grp[k]], float(np.mean(fights[k][:K])), prior_loo(k),
            float(np.mean(fights[k][K:])))
           for k in sel if len(fights[k]) > K]
    if len(rec) < 10:
        continue
    sm = np.array([r[0] for r in rec]); cur = np.array([r[1] for r in rec])
    pr = np.array([r[2] for r in rec]); rest = np.array([r[3] for r in rec])
    row = [K, len(rec)]
    best = (1e9, None)
    for m in [0, 1, 2, 3, 5, 8, 15, 30, 1e6]:
        bl = (K * cur + m * pr) / (K + m)
        r = float(np.sqrt(np.mean(((bl - rest) / sm) ** 2)))
        row.append(r)
        if r < best[0]:
            best = (r, m)
    row.append(best[1])
    tab.append(row)
p("")
p(table(["k", "fights"] + [f"m={m}" for m in [0, 1, 2, 3, 5, 8, 15, 30, "inf"]] + ["best m"], tab))
p("  m=0 is the pure current-fight mean; m=inf is the pure species prior.")

# ------------------------------------------------- per-species, the >=4 cohort
p("")
p("=" * 78)
p("Q3e. THE SAME, PER SPECIES (>=4-blow cohort, k=3, species with >=8 fights)")
p("=" * 78)
p("Guards against the all-species answer being a rat answer.")
K = 3
by = collections.defaultdict(list)
for k in sel:
    if len(fights[k]) > K:
        by[grp[k]].append((sp_sum[grp[k]] / sp_n[grp[k]], float(np.mean(fights[k][:K])),
                           prior_loo(k), float(np.mean(fights[k][K:]))))
tab = []
for g, rec in sorted(by.items(), key=lambda kv: -len(kv[1])):
    if len(rec) < 8:
        continue
    sm = np.array([r[0] for r in rec]); cur = np.array([r[1] for r in rec])
    pr = np.array([r[2] for r in rec]); rest = np.array([r[3] for r in rec])
    ec = np.abs(cur - rest) / sm; ep = np.abs(pr - rest) / sm
    tab.append([g, len(rec), float(sm[0]), float(np.median(ec)), float(np.median(ep)),
                float((ec < ep).mean())])
p("")
p(table(["species", "fights", "species_mean", "MAE_cur/REST", "MAE_prior/REST", "cur wins"], tab))

# ---------------------------------- how much variance is between vs within fight
p("")
p("=" * 78)
p("Q3f. WHY -- the variance budget of a single blow")
p("=" * 78)
p("""If almost all the variance of a blow is WITHIN a fight, no number of blows
from this fight tells you anything the species mean did not. One-way
decomposition over fights within each species (fights with >=3 landed blows).""")
tab = []
for g in ["rats", "snakes", "zombies", "banshees", "dwarves", "thieves"]:
    fl = [v for k, v in fights.items() if grp[k] == g and len(v) >= 3]
    if len(fl) < 6:
        continue
    allv = np.concatenate([np.array(v) for v in fl])
    gm = allv.mean(); N = len(allv)
    ssb = sum(len(v) * (np.mean(v) - gm) ** 2 for v in fl)
    ssw = sum(((np.array(v) - np.mean(v)) ** 2).sum() for v in fl)
    a = len(fl)
    msb = ssb / (a - 1); msw = ssw / (N - a)
    n0 = (N - sum(len(v) ** 2 for v in fl) / N) / (a - 1)
    vb = max((msb - msw) / n0, 0.0)
    tab.append([g, a, N, gm, float(np.sqrt(msw)), float(np.sqrt(vb)),
                vb / (vb + msw), msb / msw])
p("")
p(table(["species", "fights", "blows", "mean", "sd_within_fight", "sd_between_fight",
         "ICC", "F=MSB/MSW"], tab))
p("""ICC is the fraction of a blow's variance attributable to WHICH FIGHT it is in.
It is the ceiling on how much a current-fight mean could ever add.""")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q3_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
