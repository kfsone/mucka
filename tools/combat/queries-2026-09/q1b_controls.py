"""Q1 controls. Two things can fake a weapon effect and both are present here.

(1) CLUSTERING. Blows are not independent; they come in fights. The blow-level
    permutation in q1_weapon.py shuffles blows, which assumes they are. A cell
    of n=34 that is really 3 fights has ~3 independent observations, not 34.
(2) WITHIN-FIGHT TIMING. npc_weapon is NULL until the announce line arrives, so
    inside one announcing fight the early blows are labelled "none" and the late
    ones "armed". Anything that drifts over a fight (the creature levelling, the
    player's stamina/str falling) loads straight onto the weapon label.
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import numpy as np

OUT = []
def p(*a):
    s = " ".join(str(x) for x in a); print(s); OUT.append(s)

cols, rows = q("""select encounter_started_at_ms, npc, npc_group, ts, npc_weapon, dmg,
                         str, dex, sta, sta_max, rung, reset_epoch_ms
                  from swings
                  where dir='in' and hit=1 and dmg is not null
                    and encounter_started_at_ms is not null
                  order by npc_group, encounter_started_at_ms, npc, ts""")
R = [dict(zip(cols, r)) for r in rows]

# --------------------------------------------------------- how many fights?
p("=" * 78)
p("Q1h. HOW MANY INDEPENDENT FIGHTS IS EACH WEAPON CELL, REALLY?")
p("=" * 78)
p("""SQL: same landed-blow set, but counting distinct (encounter_started_at_ms, npc)
fight keys and distinct npc instance names per cell.""")
cell = collections.defaultdict(lambda: {"d": [], "f": set(), "i": set()})
for r in R:
    k = (r["npc_group"], r["npc_weapon"] or "<none announced>")
    cell[k]["d"].append(r["dmg"])
    cell[k]["f"].add((r["encounter_started_at_ms"], r["npc"]))
    cell[k]["i"].add(r["npc"])
tab = []
for (g, w), v in sorted(cell.items(), key=lambda kv: -len(kv[1]["d"])):
    if len(v["d"]) < 10:
        continue
    tab.append([g, w, len(v["d"]), len(v["f"]), len(v["i"]),
                len(v["d"]) / len(v["f"]), float(np.mean(v["d"]))])
p("")
p(table(["species", "npc_weapon", "n_blows", "n_fights", "n_instances", "blows/fight", "mean"], tab))

# ------------------------------------------- cluster (fight-level) permutation
def cluster_perm(groupA, groupB, iters=20000, seed=5):
    """Shuffle FIGHT labels, not blow labels. groupX = list of per-fight blow lists."""
    rng = np.random.default_rng(seed)
    a = np.concatenate(groupA); b = np.concatenate(groupB)
    obs = a.mean() - b.mean()
    pool = groupA + groupB; na = len(groupA)
    cnt = 0
    for _ in range(iters):
        idx = rng.permutation(len(pool))
        x = np.concatenate([pool[i] for i in idx[:na]])
        y = np.concatenate([pool[i] for i in idx[na:]])
        if abs(x.mean() - y.mean()) >= abs(obs) - 1e-12:
            cnt += 1
    return obs, (cnt + 1) / (iters + 1)

p("")
p("=" * 78)
p("Q1i. THE SAME CONTRASTS, PERMUTING WHOLE FIGHTS")
p("=" * 78)
p("""A fight is assigned to the armed arm if npc_weapon is ever non-null in it.
Blows from mixed fights are kept whole with their fight -- this is the honest
unit of independence. Test statistic unchanged (difference of blow means).""")
byfight = collections.defaultdict(lambda: {"d": [], "w": set(), "g": None})
for r in R:
    k = (r["encounter_started_at_ms"], r["npc"])
    byfight[k]["d"].append(r["dmg"]); byfight[k]["g"] = r["npc_group"]
    if r["npc_weapon"]:
        byfight[k]["w"].add(r["npc_weapon"])
res = []
for g in ["zombies", "thieves", "ogres", "giants", "goblins"]:
    A = [np.array(v["d"], float) for v in byfight.values() if v["g"] == g and v["w"]]
    B = [np.array(v["d"], float) for v in byfight.values() if v["g"] == g and not v["w"]]
    if len(A) < 3 or len(B) < 3:
        res.append([g, len(A), sum(len(x) for x in A), len(B), sum(len(x) for x in B),
                    None, None, "too few fights"])
        continue
    obs, pv = cluster_perm(A, B)
    a = np.concatenate(A); b = np.concatenate(B)
    res.append([g, len(A), len(a), len(B), len(b), a.mean(), b.mean(), obs, pv])
p("")
p(table(["species", "fights_armed", "blows_armed", "fights_none", "blows_none",
         "mean_armed", "mean_none", "diff", "p_cluster"],
        [r if len(r) == 9 else r[:7] + [r[7], ""] for r in res]))

# ----------------------------------------- within-announcing-fight before/after
p("")
p("=" * 78)
p("Q1j. WITHIN ONE FIGHT: BLOWS BEFORE THE ANNOUNCE vs BLOWS AFTER")
p("=" * 78)
p("""Restricted to fights that announce a weapon AND have >=1 landed blow on each
side of the announce. This holds the creature instance, the player's kit, the
room and the reset phase fixed -- it is the only contrast in the corpus where
"picked up a weapon" is the thing that changed.""")
pairsA, pairsB, detail = [], [], []
for k, v in byfight.items():
    pass
seq = collections.defaultdict(list)
for r in R:
    seq[(r["encounter_started_at_ms"], r["npc"])].append(r)
for k, rs in seq.items():
    pre = [r["dmg"] for r in rs if not r["npc_weapon"]]
    post = [r["dmg"] for r in rs if r["npc_weapon"]]
    if pre and post:
        detail.append([rs[0]["npc_group"], rs[0]["npc"],
                       sorted({r["npc_weapon"] for r in rs if r["npc_weapon"]})[0],
                       len(pre), float(np.mean(pre)), len(post), float(np.mean(post)),
                       float(np.mean(post) - np.mean(pre))])
        pairsA.append(np.mean(post)); pairsB.append(np.mean(pre))
if detail:
    p("")
    p(table(["species", "npc", "weapon", "n_pre", "mean_pre", "n_post", "mean_post", "delta"],
            sorted(detail, key=lambda r: r[0])))
    d = np.array(pairsA) - np.array(pairsB)
    rng = np.random.default_rng(9)
    obs = d.mean()
    # paired sign-flip permutation
    cnt = sum(1 for _ in range(20000)
              if abs((d * rng.choice([-1.0, 1.0], len(d))).mean()) >= abs(obs) - 1e-12)
    p(f"\nPaired fights: {len(d)}   mean(post-pre) = {obs:+.2f} dmg   "
      f"median = {np.median(d):+.2f}   sign-flip p = {(cnt+1)/20001:.3f}")
    p(f"Fights where post > pre: {int((d>0).sum())} / {len(d)}")
else:
    p("\nNo fight has landed blows on both sides of the announce.")

# ----------------------------------- is the armed arm a different player-state?
p("")
p("=" * 78)
p("Q1k. IS THE ARMED ARM CONFOUNDED WITH PLAYER STATE OR RESET PHASE?")
p("=" * 78)
p("""Same rows, but describing the covariates instead of the outcome. If the armed
zombie blows arrive at a different str/dex/stamina/creature-rung than the
unarmed ones, the 'weapon' difference is partly those.""")
tabc = []
for g in ["zombies", "thieves"]:
    for lab, sel in [("armed", lambda r: r["npc_weapon"]), ("none", lambda r: not r["npc_weapon"])]:
        rs = [r for r in R if r["npc_group"] == g and sel(r)]
        if not rs:
            continue
        f = lambda key: float(np.mean([r[key] for r in rs if r[key] is not None]))
        tabc.append([g, lab, len(rs), f("str"), f("dex"), f("sta"), f("sta_max"),
                     f("rung"), len({r["reset_epoch_ms"] for r in rs}),
                     len({r["npc"] for r in rs})])
p("")
p(table(["species", "arm", "n", "str", "dex", "sta", "sta_max", "rung", "n_resets", "n_instances"], tabc))

open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "q1b_out.txt"), "w",
     encoding="utf-8").write("\n".join(OUT))
