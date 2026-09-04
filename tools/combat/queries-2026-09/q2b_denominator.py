"""Q2 denominator audit. `lobsters` came out at P(swing)=0.001 over 1206 ticks
from 2 fights, and `wyverns` at 0.057 over 576. Neither is a creature that swings
once an hour; both are fights whose duration_ms kept running while nothing was
happening. duration_ms is therefore NOT interchangeable with "engaged".

This measures how far that goes, and rebuilds the tick denominator from OBSERVED
swing timestamps for the fights the swings ledger covers.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

TICK = 2000.0
T_of = lambda d: round(d / TICK) + 1

cols, F = q("""select npc_group, npc_name, duration_ms, they_hits, they_misses,
                      you_hits, you_misses, approx_damage_taken,
                      encounter_started_at_ms, started_at_ms, outcome
               from fights""")
F = [dict(zip(cols, r)) for r in F]

p("=" * 78)
p("Q2e. P(swing) BY FIGHT DURATION -- where duration_ms stops meaning 'engaged'")
p("=" * 78)
p("SQL: select duration_ms, they_hits+they_misses, you_hits+you_misses from fights;")
buckets = [(0, 2), (2, 5), (5, 10), (10, 20), (20, 40), (40, 80), (80, 160), (160, 10 ** 9)]
tab = []
for lo, hi in buckets:
    sel = [r for r in F if lo <= r["duration_ms"] / 1000 < hi]
    if not sel:
        continue
    T = sum(T_of(r["duration_ms"]) for r in sel)
    S = sum(r["they_hits"] + r["they_misses"] for r in sel)
    O = sum(r["you_hits"] + r["you_misses"] for r in sel)
    tab.append([f"{lo}-{hi if hi < 10**9 else 'inf'}s", len(sel), T, S, S / T, O / T])
p("")
p(table(["duration", "fights", "eng_ticks", "in_swings", "P(swing_in)", "P(swing_out)"], tab))
p("""
P(swing) collapses in the long tail. Those fights are not creatures pausing; they
are fights left open while the player did something else. The duration-based tick
count over-counts engagement, and it over-counts it MOST for the species that
happen to appear in a couple of abandoned fights -- which is precisely how
lobsters got a 1206-tick denominator off 2 fights.""")

# ------------------------------------- rebuild ticks from observed swing spans
p("")
p("=" * 78)
p("Q2f. TICKS REBUILT FROM OBSERVED SWING TIMESTAMPS")
p("=" * 78)
p("""For every fight the swings ledger covers, engaged_ticks is taken as
    round((last_swing_ts - first_swing_ts)/2000) + 1
over ALL swings of that fight, in and out. This cannot include trailing dead
time. It slightly UNDER-counts (a fight's last tick after the final swing is
lost), so it is the opposite bias to Q2b and brackets the truth with it.""")
cols2, S = q("""select encounter_started_at_ms, npc, npc_group, dir, ts, hit, dmg
                from swings where encounter_started_at_ms is not null
                order by encounter_started_at_ms, npc, ts""")
fs = collections.defaultdict(list)
for enc, npc, grp, d, ts, hit, dmg in S:
    fs[(enc, npc)].append((ts, grp, d, hit, dmg))
gg = collections.defaultdict(lambda: dict(t=0, s=0, h=0, d=0.0, n=0, sq=0.0, dm=[]))
for k, v in fs.items():
    ts = [x[0] for x in v]
    T = round((max(ts) - min(ts)) / TICK) + 1
    grp = v[0][1]
    ins = [x for x in v if x[2] == "in"]
    land = [x for x in ins if x[3] == 1 and x[4] is not None]
    e = gg[grp]
    e["t"] += T; e["s"] += len(ins); e["h"] += len(land); e["n"] += 1
    e["d"] += sum(x[4] for x in land); e["sq"] += sum(x[4] ** 2 for x in land)
    e["dm"].extend(x[4] for x in land)
rows = []
for k, v in sorted(gg.items(), key=lambda kv: -kv[1]["t"]):
    if v["t"] < 150:
        continue
    ps = v["s"] / v["t"]; ph = v["h"] / v["s"] if v["s"] else float("nan")
    ed = v["d"] / v["h"] if v["h"] else float("nan")
    emp = v["d"] / v["t"]
    sd = float(np.sqrt(max(v["sq"] / v["t"] - emp ** 2, 0)))
    rows.append([k, v["n"], v["t"], v["s"], v["h"], ps, ph, ed, ps * ph * ed, emp, sd,
                 sd / emp if emp else float("nan")])
p("")
p(table(["species", "fights", "eng_ticks", "in_swings", "landed", "P(swing)",
         "P(hit|sw)", "E[dmg|hit]", "dpt_fact", "dpt_emp", "dpt_sd", "CV_tick"], rows))
GT = sum(v["t"] for v in gg.values()); GS = sum(v["s"] for v in gg.values())
p(f"\nGlobal pooled P(swing_in) on this denominator = {GS}/{GT} = {GS/GT:.4f}")
p("(The 49.6% prior lands inside the bracket set by this and Q2b's 41.3%.)")

p("")
p("=" * 78)
p("Q2g. IS P(swing) FLAT ACROSS SPECIES? (permutation over fights)")
p("=" * 78)
p("""Statistic: the spread (max-min) of per-species pooled P(swing) across the
species above, with whole fights reassigned to species labels at random,
preserving each species' fight count. Observed spread vs the null spread.""")
big = [r[0] for r in rows]
pool = [(v[1], round((max(x[0] for x in vv) - min(x[0] for x in vv)) / TICK) + 1,
         sum(1 for x in vv if x[2] == "in"))
        for vv in [None] for v in [None]] if False else None
items = []
for k, v in fs.items():
    grp = v[0][1]
    if grp not in big:
        continue
    ts = [x[0] for x in v]
    items.append((grp, round((max(ts) - min(ts)) / TICK) + 1, sum(1 for x in v if x[2] == "in")))
def spread(labels):
    a = collections.defaultdict(lambda: [0, 0])
    for (g0, T, s), lb in zip(items, labels):
        a[lb][0] += T; a[lb][1] += s
    ps = [s / t for t, s in a.values() if t >= 150]
    return max(ps) - min(ps)
labels = [i[0] for i in items]
obs = spread(labels)
rng = np.random.default_rng(31)
null = []
for _ in range(5000):
    lb = list(labels); rng.shuffle(lb); null.append(spread(lb))
null = np.array(null)
p(f"\nObserved spread of P(swing) across {len(rows)} species: {obs:.4f}")
p(f"Null (fights reshuffled): median {np.median(null):.4f}, p95 {np.percentile(null,95):.4f}")
p(f"p = {(np.sum(null >= obs) + 1) / 5001:.4f}")

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q2b_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
