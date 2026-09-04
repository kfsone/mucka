"""Q2 -- damage per ENGAGED TICK, per species.

Tick definition (stated, because everything downstream is a ratio to it):
    engaged_ticks(fight) = round(duration_ms / 2000) + 1
one fight = one row of `fights` = the player vs ONE creature inside one encounter;
a pack fight contributes one such row per creature, which is what per-species
"while I was engaged with this thing" needs.

`fights.approx_damage_taken` was verified against the swings ledger: on the 999
fights in the swings era with they_hits>0, the 916 that have swing rows at all
agree EXACTLY on both hit count and damage total. So the fights table is the
same exact stamina-derived damage, over a longer span. It is used for the means;
the swings table supplies per-blow values where a variance is needed.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

TICK = 2000.0

# ------------------------------------------------ tick definition sensitivity
p("=" * 78)
p("Q2a. TICK-COUNT DEFINITION AND ITS SENSITIVITY")
p("=" * 78)
p("""SQL: select duration_ms, they_hits, they_misses, you_hits, you_misses from fights;""")
cols, F = q("""select npc_group, duration_ms, they_hits, they_misses, you_hits, you_misses,
                      approx_damage_taken, encounter_started_at_ms, npc_name, started_at_ms
               from fights""")
F = [dict(zip(cols, r)) for r in F]
defs = {"round(d/2000)+1": lambda d: round(d / TICK) + 1,
        "floor(d/2000)+1": lambda d: int(d // TICK) + 1,
        "round(d/2000), min 1": lambda d: max(1, round(d / TICK))}
tab = []
for name, f in defs.items():
    T = sum(f(r["duration_ms"]) for r in F)
    S = sum(r["they_hits"] + r["they_misses"] for r in F)
    O = sum(r["you_hits"] + r["you_misses"] for r in F)
    tab.append([name, T, S, S / T, O, O / T])
p("")
p(table(["tick definition", "engaged_ticks", "in_swings", "P(swing_in)", "out_swings", "P(swing_out)"], tab))
p("""
The ~49.6% prior sits between these. Everything below uses round(d/2000)+1, the
most conservative (largest denominator), so every dpt figure here is a floor.
Rescaling to another definition is a single multiply: the ratios between species
are unchanged.""")

TICKS = lambda r: round(r["duration_ms"] / TICK) + 1

over = sum(1 for r in F if r["they_hits"] + r["they_misses"] > TICKS(r))
p(f"\nFights where in-swings EXCEED the tick count (model violation): {over} / {len(F)} "
  f"({100*over/len(F):.1f}%)")

# ------------------------------------------------------ per-species factoring
p("")
p("=" * 78)
p("Q2b. FACTORED vs EMPIRICAL DAMAGE PER ENGAGED TICK")
p("=" * 78)
p("""Per species, pooled over fights:
    P(swing)      = (they_hits + they_misses) / engaged_ticks
    P(hit|swing)  = they_hits / (they_hits + they_misses)
    E[dmg|hit]    = approx_damage_taken / they_hits
    dpt_factored  = the product of the three
    dpt_empirical = approx_damage_taken / engaged_ticks
These are algebraically the same number when all three factors come from the
same rows, so the row 'dpt_fact' below is an identity check (it must match to
rounding) and NOT evidence. The informative column is dpt_flat: the same product
with P(swing) replaced by the GLOBAL pooled swing rate. Where dpt_flat departs
from dpt_emp, that species' swing rate is NOT the global one.""")
g = collections.defaultdict(lambda: dict(t=0, s=0, h=0, d=0.0, n=0))
for r in F:
    k = r["npc_group"]
    g[k]["t"] += TICKS(r); g[k]["s"] += r["they_hits"] + r["they_misses"]
    g[k]["h"] += r["they_hits"]; g[k]["d"] += r["approx_damage_taken"]; g[k]["n"] += 1
GT = sum(v["t"] for v in g.values()); GS = sum(v["s"] for v in g.values())
pswing_global = GS / GT
p(f"\nGlobal pooled P(swing_in) = {GS}/{GT} = {pswing_global:.4f}")

# per-blow variance from the swings ledger, for the per-tick SD
cols2, SW = q("""select npc_group, dmg from swings
                 where dir='in' and hit=1 and dmg is not null""")
blows = collections.defaultdict(list)
for grp, d in SW:
    blows[grp].append(d)

rows = []
for k, v in sorted(g.items(), key=lambda kv: -kv[1]["t"]):
    if v["t"] < 150:
        continue
    ps = v["s"] / v["t"]
    ph = v["h"] / v["s"] if v["s"] else float("nan")
    ed = v["d"] / v["h"] if v["h"] else float("nan")
    emp = v["d"] / v["t"]
    rows.append([k, v["n"], v["t"], v["s"], v["h"], ps, ph, ed,
                 ps * ph * ed, emp, pswing_global * ph * ed])
p("")
p(table(["species", "fights", "eng_ticks", "in_swings", "landed", "P(swing)",
         "P(hit|sw)", "E[dmg|hit]", "dpt_fact", "dpt_emp", "dpt_flat"], rows))
p("\n(dpt_fact == dpt_emp to rounding, as it must. dpt_flat vs dpt_emp is the test.)")
dev = [(r[0], r[10] / r[9] - 1, r[5]) for r in rows]
p("\nSpecies whose own swing rate moves dpt by >15% vs the flat-rate assumption:")
for k, e, ps in sorted(dev, key=lambda x: -abs(x[1])):
    flag = "  <-- " if abs(e) > 0.15 else "      "
    p(f"{flag}{k:12s} P(swing)={ps:.3f}  dpt_flat/dpt_emp-1 = {e:+.1%}")

# ------------------------------------------------------------ per-tick SD
p("")
p("=" * 78)
p("Q2c. SD OF PER-TICK DAMAGE (zero-damage ticks INCLUDED)")
p("=" * 78)
p("""What a variance display actually has to draw. A tick's damage is 0 on a
non-swing or a miss, and dmg on a landed blow, so over T ticks with L landed
blows d_1..d_L:
    mean = sum(d)/T
    E[x^2] = sum(d^2)/T
    sd   = sqrt(E[x^2] - mean^2)
sum(d^2) comes from the swings ledger (per-blow values); T and sum(d) from the
fights table. Ratio-of-cohorts is handled by scaling: the swings ledger's own
sum(d) is used with the swings-era tick total for that species.""")
# swings-era cohort only, so numerator and denominator are the same rows
minsw = q("select min(ts) from swings")[1][0][0]
gs = collections.defaultdict(lambda: dict(t=0, h=0, d=0.0, s=0))
for r in F:
    if r["started_at_ms"] < minsw:
        continue
    k = r["npc_group"]
    gs[k]["t"] += TICKS(r); gs[k]["h"] += r["they_hits"]; gs[k]["d"] += r["approx_damage_taken"]
    gs[k]["s"] += r["they_hits"] + r["they_misses"]
rows = []
for k, v in sorted(gs.items(), key=lambda kv: -kv[1]["t"]):
    if v["t"] < 150 or k not in blows:
        continue
    b = np.asarray(blows[k], float)
    T = v["t"]
    # scale the ledger's blow set to the fights-table landed count for this cohort
    m2 = float((b ** 2).mean())
    L = v["h"]
    mean = L * float(b.mean()) / T
    ex2 = L * m2 / T
    sd = float(np.sqrt(max(ex2 - mean ** 2, 0)))
    rows.append([k, T, L, float(b.mean()), float(b.std(ddof=1)), mean, sd, sd / mean if mean else float("nan"),
                 float(np.percentile(b, 95)), float(b.max())])
p("")
p(table(["species", "eng_ticks", "landed", "mean|hit", "sd|hit", "dpt_mean", "dpt_sd",
         "CV_tick", "p95|hit", "max|hit"], rows))
p("""
CV_tick (sd/mean of per-tick damage) is the number that decides whether a live
"damage per tick" readout can be drawn as a point estimate at all.""")

# -------------------------------------- how many ticks to a stable dpt estimate
p("")
p("=" * 78)
p("Q2d. HOW MANY ENGAGED TICKS BEFORE dpt IS KNOWN TO +/-20%?")
p("=" * 78)
p("""Standard error of a mean over n ticks is dpt_sd/sqrt(n); solving
sd/sqrt(n) <= 0.2*mean gives n = (5*CV)^2.""")
p("")
p(table(["species", "CV_tick", "ticks for +/-20%", "seconds at 2s/tick"],
        [[r[0], r[7], round((5 * r[7]) ** 2), round((5 * r[7]) ** 2) * 2] for r in rows]))

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q2_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
